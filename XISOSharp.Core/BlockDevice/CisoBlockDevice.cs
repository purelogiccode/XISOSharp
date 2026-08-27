using System.Buffers.Binary;

namespace XISOSharp.BlockDevice;

/// <summary>
/// Block device that presents a CISO/CSO file as an uncompressed block device,
/// mirroring <c>xdvdfs-cli/src/img.rs::CSOBlockDevice</c> and <c>ciso::read::CSOReader::read_offset</c>.
/// Decompresses sectors on demand and caches the last decompressed block.
/// </summary>
public sealed class CisoBlockDevice : IBlockDevice
{
    private readonly FileStream _csoFs;
    private readonly uint _blockSize;
    private readonly byte _version;
    private readonly byte _align;
    private readonly uint[] _index;
    private readonly bool _leaveOpen;

    // Simple single-sector cache to avoid re-decompressing same sector repeatedly during tree walk
    private long _cachedSector = -1;
    private byte[]? _cachedData;

    /// <summary>Opens a CISO file as a block device.</summary>
    public CisoBlockDevice(string csoPath) : this(
        new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536), leaveOpen: false)
    {
    }

    /// <summary>Wraps an open CISO stream.</summary>
    public CisoBlockDevice(FileStream csoFs, bool leaveOpen = false)
    {
        _csoFs = csoFs ?? throw new ArgumentNullException(nameof(csoFs));
        if (!csoFs.CanSeek) throw new ArgumentException("CISO stream must be seekable", nameof(csoFs));
        _leaveOpen = leaveOpen;

        Span<byte> hdr = stackalloc byte[24];
        csoFs.Seek(0, SeekOrigin.Begin);
        ReadExact(csoFs, hdr);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
        uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
        Length = (long)BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..16]);
        _blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..20]);
        _version = hdr[20];
        _align = hdr[21];

        if (magic != CisoWriter.Magic) throw new InvalidDataException("Not a CISO file (bad magic)");
        if (hsize != CisoWriter.HeaderSize) throw new InvalidDataException($"Unsupported CISO header size {hsize}");
        if (_version != CisoWriter.VersionDeflate && _version != CisoWriter.VersionLz4)
            throw new InvalidDataException($"Unsupported CISO version {_version}");
        if (_blockSize != 2048) throw new InvalidDataException($"Unsupported CISO block size {_blockSize}");

        long totalBlocks = (Length + _blockSize - 1) / _blockSize;
        long indexLen = totalBlocks + 1;
        _index = new uint[indexLen];
        Span<byte> leBuf = stackalloc byte[4];
        for (long i = 0; i < indexLen; i++)
        {
            ReadExact(csoFs, leBuf);
            _index[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }
    }

    /// <inheritdoc/>
    public long Length { get; }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
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
            var compBuf = new byte[dataLen];
            _csoFs.Seek((long)off, SeekOrigin.Begin);
            int n = 0;
            while (n < dataLen)
            {
                int r = _csoFs.Read(compBuf, n, (int)(dataLen - n));
                if (r == 0) throw new EndOfStreamException($"Unexpected EOF at compressed sector {sector}");
                n += r;
            }

            data = DecompressWithTrim(compBuf, _align, _version);
            if (data.Length != _blockSize)
            {
                // Pad/truncate to block size for last partial block? For CISO, last block is still full 2048, but file may be truncated
                if (data.Length < _blockSize)
                {
                    var tmp = new byte[_blockSize];
                    Array.Copy(data, tmp, data.Length);
                    data = tmp;
                }
                else if (data.Length > _blockSize)
                {
                    Array.Resize(ref data, (int)_blockSize);
                }
            }
        }

        _cachedSector = sector;
        _cachedData = data;
        return data;
    }

    private static byte[] DecompressWithTrim(byte[] compBuf, byte align, byte version)
    {
        int maxTrim = align == 0 ? 0 : (1 << align) - 1;
        maxTrim = Math.Max(maxTrim, 3);
        for (int trim = 0; trim <= maxTrim && trim <= compBuf.Length; trim++)
        {
            int tryLen = compBuf.Length - trim;
            if (tryLen <= 0) continue;
            bool tailZero = true;
            for (int z = tryLen; z < compBuf.Length; z++)
                if (compBuf[z] != 0)
                {
                    tailZero = false;
                    break;
                }

            if (!tailZero && trim != 0) continue;
            try
            {
                byte[] dec = version == CisoWriter.VersionDeflate
                    ? CisoReaderDeflate(compBuf.AsSpan(0, tryLen))
                    : CisoReaderLz4(compBuf.AsSpan(0, tryLen));
                if (dec.Length == 2048) return dec;
            }
            catch
            {
                // ignored
            }
        }

        // fallback
        return version == CisoWriter.VersionDeflate
            ? CisoReaderDeflate(compBuf)
            : CisoReaderLz4(compBuf);
    }

    private static byte[] CisoReaderDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static byte[] CisoReaderLz4(ReadOnlySpan<byte> data)
    {
        try
        {
            var dec = CisoReaderDeflate(data);
            if (dec.Length == 2048) return dec;
        }
        catch
        {
            // ignored
        }

        return Lz4BlockDecompress(data, 2048);
    }

    private static byte[] Lz4BlockDecompress(ReadOnlySpan<byte> src, int expectedSize)
    {
        var dst = new byte[expectedSize];
        int srcPos = 0, dstPos = 0;
        while (srcPos < src.Length && dstPos < expectedSize)
        {
            byte token = src[srcPos++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                byte len;
                do
                {
                    if (srcPos >= src.Length) break;
                    len = src[srcPos++];
                    litLen += len;
                } while (len == 255);
            }

            if (srcPos + litLen > src.Length) throw new InvalidDataException("LZ4 literal overrun");
            src.Slice(srcPos, litLen).CopyTo(dst.AsSpan(dstPos, litLen));
            srcPos += litLen;
            dstPos += litLen;
            if (dstPos >= expectedSize || srcPos >= src.Length) break;
            if (srcPos + 2 > src.Length) throw new InvalidDataException("LZ4 offset missing");
            int offset = src[srcPos++] | (src[srcPos++] << 8);
            if (offset == 0) throw new InvalidDataException("LZ4 offset zero");
            int mLen = token & 0x0F;
            if (mLen == 15)
            {
                byte len;
                do
                {
                    if (srcPos >= src.Length) break;
                    len = src[srcPos++];
                    mLen += len;
                } while (len == 255);
            }

            mLen += 4;
            if (mLen > expectedSize - dstPos) mLen = expectedSize - dstPos;
            for (int i = 0; i < mLen; i++) dst[dstPos + i] = dst[dstPos - offset + i];
            dstPos += mLen;
        }

        if (dstPos != expectedSize) throw new InvalidDataException($"LZ4 decompressed {dstPos} != {expectedSize}");
        return dst;
    }

    private static void ReadExact(FileStream fs, Span<byte> buf)
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