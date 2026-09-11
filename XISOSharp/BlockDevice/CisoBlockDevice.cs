using System.Buffers.Binary;
using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// Block device that presents a CISO/CSO file as an uncompressed block device,
/// mirroring <c>xdvdfs-cli/src/img.rs::CSOBlockDevice</c> and <c>ciso::read::CSOReader::read_offset</c>.
/// Accepts single files and split <c>*.1.cso</c>/<c>*.2.cso</c>… part sets.
/// Decompresses sectors on demand and caches the last decompressed block.
/// </summary>
public sealed class CisoBlockDevice : IBlockDevice
{
    private readonly Stream _csoFs;
    private readonly uint _blockSize;
    private readonly byte _version;
    private readonly byte _align;
    private readonly uint[] _index;
    private readonly bool _leaveOpen;

    // Simple single-sector cache to avoid re-decompressing same sector repeatedly during tree walk
    private long _cachedSector = -1;
    private byte[]? _cachedData;

    /// <summary>Opens a CISO file (single or split <c>*.1.cso</c> parts) as a block device.</summary>
    /// <remarks>
    /// The file handle opened here is owned by this device: if header validation
    /// throws, it is disposed before the exception propagates (no handle leak).
    /// </remarks>
    public CisoBlockDevice(string csoPath)
    {
        Stream fs = OpenCsoStream(csoPath);
        try
        {
            // Field assignments stay inline: get-only/readonly members cannot be
            // assigned from a helper method.
            (long UncompressedSize, uint BlockSize, byte Version, byte Align, int IndexEntryCount) header =
                ReadHeader(fs);
            _csoFs = fs;
            _leaveOpen = false;
            Length = header.UncompressedSize;
            _blockSize = header.BlockSize;
            _version = header.Version;
            _align = header.Align;
            _index = ReadIndex(fs, header.IndexEntryCount);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an open CISO file stream.</summary>
    public CisoBlockDevice(FileStream csoFs, bool leaveOpen = false) : this((Stream)csoFs, leaveOpen)
    {
    }

    /// <summary>Wraps an open CISO stream (e.g. the composite stream over split parts).</summary>
    /// <remarks>
    /// A caller-provided stream is never disposed when validation throws — ownership
    /// transfers only on successful construction (<see cref="Dispose"/> then honors
    /// <paramref name="leaveOpen"/>).
    /// </remarks>
    public CisoBlockDevice(Stream csoFs, bool leaveOpen = false)
    {
        _csoFs = csoFs ?? throw new ArgumentNullException(nameof(csoFs));
        if (!csoFs.CanSeek) throw new ArgumentException("CISO stream must be seekable", nameof(csoFs));
        _leaveOpen = leaveOpen;

        (long UncompressedSize, uint BlockSize, byte Version, byte Align, int IndexEntryCount) header =
            ReadHeader(csoFs);
        Length = header.UncompressedSize;
        _blockSize = header.BlockSize;
        _version = header.Version;
        _align = header.Align;
        _index = ReadIndex(csoFs, header.IndexEntryCount);
    }

    /// <summary>Validated CISO header: all attacker-controlled sizes range-checked.</summary>
    private static (long UncompressedSize, uint BlockSize, byte Version, byte Align, int IndexEntryCount)
        ReadHeader(Stream csoFs)
    {
        Span<byte> hdr = stackalloc byte[24];
        csoFs.Seek(0, SeekOrigin.Begin);
        ReadExact(csoFs, hdr);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
        ulong claimedSize = BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..16]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..20]);
        byte version = hdr[20];
        byte align = hdr[21];

        if (magic != CisoWriter.Magic) throw new InvalidDataException("Not a CISO file (bad magic)");
        if (hsize != CisoWriter.HeaderSize) throw new InvalidDataException($"Unsupported CISO header size {hsize}");
        if (version != CisoWriter.VersionDeflate && version != CisoWriter.VersionLz4)
            throw new InvalidDataException($"Unsupported CISO version {version}");
        if (blockSize != 2048) throw new InvalidDataException($"Unsupported CISO block size {blockSize}");

        // The u64 claim must fit a long before any arithmetic: otherwise Length wraps
        // negative and the index math below goes negative with it.
        if (claimedSize > (ulong)long.MaxValue)
            throw new InvalidDataException($"CISO uncompressed size {claimedSize} exceeds supported range");
        long length = (long)claimedSize;

        long totalBlocks = (length + blockSize - 1) / blockSize;
        long indexLen = totalBlocks + 1;
        // Array lengths are int: reject absurd claims before allocating.
        if (indexLen > int.MaxValue)
            throw new InvalidDataException($"CISO index too large ({indexLen} entries) for claimed size {length}");
        int indexCount = (int)indexLen;
        // The index table itself lives in this stream: 4 bytes per entry past the header.
        if ((long)indexCount * 4 > csoFs.Length - hdr.Length)
            throw new InvalidDataException("CISO index table exceeds stream length");

        return (length, blockSize, version, align, indexCount);
    }

    private static uint[] ReadIndex(Stream csoFs, int indexCount)
    {
        uint[] index = new uint[indexCount];
        Span<byte> leBuf = stackalloc byte[4];
        for (int i = 0; i < indexCount; i++)
        {
            ReadExact(csoFs, leBuf);
            index[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }

        return index;
    }

    /// <summary>Opens a CISO source: a plain <c>.cso</c> file or the composite stream over split parts.</summary>
    private static Stream OpenCsoStream(string path)
    {
        if (CisoSplitFile.IsSplitPath(path))
        {
            List<FileStream> parts = CisoSplitFile.OpenParts(path);
            if (parts.Count == 0) throw new FileNotFoundException($"CSO not found: {path}");
            return parts.Count == 1 ? parts[0] : new CisoSplitInputStream(parts);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
    }

    /// <inheritdoc/>
    public long Length { get; }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length) return 0;
        long toRead = Math.Min(buffer.Length, Length - offset);
        long sector = offset / _blockSize;
        long sectorOff = offset % _blockSize;
        int bufPos = 0;
        int remaining = (int)toRead;

        while (remaining > 0)
        {
            byte[] sectorData = GetSector(sector);
            int copy = (int)Math.Min(remaining, _blockSize - sectorOff);
            sectorData.AsSpan((int)sectorOff, copy).CopyTo(buffer.Slice(bufPos, copy));
            bufPos += copy;
            remaining -= copy;
            sector++;
            sectorOff = 0;
        }

        if (bufPos < buffer.Length)
            buffer[bufPos..].Clear();
        return (int)toRead;
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("CISO block device is read-only");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_leaveOpen) _csoFs.Dispose();
    }

    private byte[] GetSector(long sector)
    {
        if (_cachedSector == sector && _cachedData != null) return _cachedData;

        uint rawEntry = _index[sector];
        uint rawNext = _index[sector + 1];
        bool isPlain = _version == CisoWriter.VersionDeflate
            ? (rawEntry & 0x80000000u) != 0
            : (rawEntry & 0x80000000u) == 0;

        ulong off = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << _align);
        ulong nextOff = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << _align);
        long dataLen = (long)(nextOff - off);

        byte[] data;
        if (isPlain)
        {
            data = new byte[_blockSize];
            _csoFs.Seek((long)off, SeekOrigin.Begin);
            int n = 0;
            while (n < _blockSize)
            {
                int r = _csoFs.Read(data, n, (int)_blockSize - n);
                if (r == 0) throw new EndOfStreamException($"Unexpected EOF at plain sector {sector}");
                n += r;
            }
        }
        else
        {
            if (dataLen <= 0) throw new InvalidDataException($"Zero-length compressed sector {sector}");

            // The last sector's index gap can round down (final entry stores position >> align);
            // extend the read to recover the true payload.
            long readLen = dataLen;
            if (sector == _index.Length - 2)
                readLen = Math.Min(dataLen + (1L << _align) - 1, _csoFs.Length - (long)off);

            byte[] compBuf = new byte[readLen];
            _csoFs.Seek((long)off, SeekOrigin.Begin);
            int n = 0;
            while (n < readLen)
            {
                int r = _csoFs.Read(compBuf, n, (int)(readLen - n));
                if (r == 0) break;
                n += r;
            }

            if (n < readLen)
                Array.Resize(ref compBuf, n);

            data = CisoReader.DecompressSector(_version, _align, compBuf, (int)_blockSize);
        }

        _cachedSector = sector;
        _cachedData = data;
        return data;
    }

    private static void ReadExact(Stream fs, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = fs.Read(buf[off..]);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
