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
    public CisoBlockDevice(string csoPath) : this(OpenCsoStream(csoPath), leaveOpen: false)
    {
    }

    /// <summary>Wraps an open CISO file stream.</summary>
    public CisoBlockDevice(FileStream csoFs, bool leaveOpen = false) : this((Stream)csoFs, leaveOpen)
    {
    }

    /// <summary>Wraps an open CISO stream (e.g. the composite stream over split parts).</summary>
    public CisoBlockDevice(Stream csoFs, bool leaveOpen = false)
    {
        _csoFs = csoFs ?? throw new ArgumentNullException(nameof(csoFs));
        if (!csoFs.CanSeek) throw new ArgumentException("CISO stream must be seekable", nameof(csoFs));
        _leaveOpen = leaveOpen;

        Span<byte> hdr = stackalloc byte[24];
        csoFs.Seek(0, SeekOrigin.Begin);
        ReadExact(csoFs, hdr);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        var hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
        Length = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..16]);
        _blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..20]);
        _version = hdr[20];
        _align = hdr[21];

        if (magic != CisoWriter.Magic) throw new InvalidDataException("Not a CISO file (bad magic)");
        if (hsize != CisoWriter.HeaderSize) throw new InvalidDataException($"Unsupported CISO header size {hsize}");
        if (_version != CisoWriter.VersionDeflate && _version != CisoWriter.VersionLz4)
            throw new InvalidDataException($"Unsupported CISO version {_version}");
        if (_blockSize != 2048) throw new InvalidDataException($"Unsupported CISO block size {_blockSize}");

        var totalBlocks = (Length + _blockSize - 1) / _blockSize;
        var indexLen = totalBlocks + 1;
        _index = new uint[indexLen];
        Span<byte> leBuf = stackalloc byte[4];
        for (long i = 0; i < indexLen; i++)
        {
            ReadExact(csoFs, leBuf);
            _index[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }
    }

    /// <summary>Opens a CISO source: a plain <c>.cso</c> file or the composite stream over split parts.</summary>
    private static Stream OpenCsoStream(string path)
    {
        if (CisoSplitFile.IsSplitPath(path))
        {
            var parts = CisoSplitFile.OpenParts(path);
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
        var toRead = Math.Min(buffer.Length, Length - offset);
        var sector = offset / _blockSize;
        var sectorOff = offset % _blockSize;
        var bufPos = 0;
        var remaining = (int)toRead;

        while (remaining > 0)
        {
            var sectorData = GetSector(sector);
            var copy = (int)Math.Min(remaining, _blockSize - sectorOff);
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

        var rawEntry = _index[sector];
        var rawNext = _index[sector + 1];
        var isPlain = _version == CisoWriter.VersionDeflate
            ? (rawEntry & 0x80000000u) != 0
            : (rawEntry & 0x80000000u) == 0;

        var off = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << _align);
        var nextOff = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << _align);
        var dataLen = (long)(nextOff - off);

        byte[] data;
        if (isPlain)
        {
            data = new byte[_blockSize];
            _csoFs.Seek((long)off, SeekOrigin.Begin);
            var n = 0;
            while (n < _blockSize)
            {
                var r = _csoFs.Read(data, n, (int)_blockSize - n);
                if (r == 0) throw new EndOfStreamException($"Unexpected EOF at plain sector {sector}");
                n += r;
            }
        }
        else
        {
            if (dataLen <= 0) throw new InvalidDataException($"Zero-length compressed sector {sector}");

            // The last sector's index gap can round down (final entry stores position >> align);
            // extend the read to recover the true payload.
            var readLen = dataLen;
            if (sector == _index.Length - 2)
                readLen = Math.Min(dataLen + (1L << _align) - 1, _csoFs.Length - (long)off);

            var compBuf = new byte[readLen];
            _csoFs.Seek((long)off, SeekOrigin.Begin);
            var n = 0;
            while (n < readLen)
            {
                var r = _csoFs.Read(compBuf, n, (int)(readLen - n));
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
        var off = 0;
        while (off < buf.Length)
        {
            var n = fs.Read(buf[off..]);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
