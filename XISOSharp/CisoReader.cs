using System.Buffers.Binary;
using System.IO.Compression;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// CISO / CSO decompression and random-access reader.
/// Handles version 1 (DEFLATE, plain=high bit) and version 2 (LZ4, compressed=high bit)
/// for interoperability with <c>ciso 0.2.1</c> / <c>xdvdfs</c> and classic tools, including
/// split <c>.1.cso</c>/<c>.2.cso</c>… part files (<c>ciso::split::SplitFileReader</c> parity).
/// </summary>
public static class CisoReader
{
    /// <summary>CISO block size in bytes (2048, matches XISO sector size).</summary>
    public const int BlockSize = 2048;

    /// <summary>CISO magic <c>0x4F534943</c> ("CISO" little-endian).</summary>
    public const uint Magic = 0x4F534943u;

    /// <summary>CISO header size in bytes (24).</summary>
    public const uint HeaderSize = 24;

    /// <summary>
    /// Returns true if <paramref name="path"/> is a CISO/CSO file (checks magic + header size).
    /// Accepts single files and the first part of a split image (<c>*.1.cso</c>).
    /// </summary>
    public static bool IsCso(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256);
            if (fs.Length < HeaderSize) return false;
            Span<byte> hdr = stackalloc byte[24];
            var n = fs.Read(hdr);
            if (n != 24) return false;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
            var hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
            var ver = hdr[20];
            return magic == Magic && hsize == HeaderSize &&
                   (ver == CisoWriter.VersionDeflate || ver == CisoWriter.VersionLz4);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decompresses a CISO/CSO file to an ISO file. Accepts split input
    /// (<c>image.1.cso</c>, with <c>image.2.cso</c>, … resolved alongside). Returns 0 on success, 1 on error.
    /// </summary>
    public static int DecompressToIso(string csoPath, string? outputIsoPath = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(csoPath)) throw new FileNotFoundException($"CSO not found: {csoPath}");

        var output = outputIsoPath ?? DeriveDefaultIsoPath(csoPath);
        if (XisoPaths.AreSamePath(csoPath, output))
            throw new IOException("Source and destination paths are the same");

        using var src = OpenReadStream(csoPath);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        DecompressStream(src, dst, progress, ct);
        Logger.Log($"Decompressed {csoPath} -> {output} ({new FileInfo(output).Length} bytes)\n");
        return 0;
    }

    /// <summary>
    /// Asynchronous variant of <see cref="DecompressToIso"/>.
    /// Decompresses a CISO/CSO file to an ISO file on a thread-pool thread.
    /// </summary>
    /// <param name="csoPath">Source CISO/CSO path (single file or split <c>*.1.cso</c> parts).</param>
    /// <param name="outputIsoPath">Destination ISO path; if <c>null</c>, derived from <paramref name="csoPath"/> (<c>.iso</c>).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error; throws on invalid arguments.</returns>
    public static async Task<int> DecompressToIsoAsync(string csoPath, string? outputIsoPath = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => DecompressToIso(csoPath, outputIsoPath, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decompresses a CISO stream to an output stream. Both must be seekable.
    /// Supports both DEFLATE (v1) and LZ4 (v2) payloads. Version 2 payloads are the per-sector
    /// LZ4 frame with the 7-byte frame header and 4-byte end mark stripped
    /// (<c>[u32 LE block info][block data]</c>, block-info high bit = uncompressed block),
    /// as produced by <c>ciso 0.2.1</c> / <c>xdvdfs compress</c>.
    /// </summary>
    public static void DecompressStream(Stream source, Stream dest,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!source.CanSeek) throw new ArgumentException("Source must be seekable", nameof(source));
        if (!dest.CanSeek) throw new ArgumentException("Destination must be seekable", nameof(dest));

        Span<byte> header = stackalloc byte[24];
        source.Seek(0, SeekOrigin.Begin);
        ReadExact(source, header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var hsize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        var version = header[20];
        var align = header[21];

        if (magic != Magic) throw new InvalidDataException("Not a CISO file (bad magic)");
        if (hsize != HeaderSize) throw new InvalidDataException($"Unsupported CISO header size {hsize} (expected 24)");
        if (version != CisoWriter.VersionDeflate && version != CisoWriter.VersionLz4)
            throw new InvalidDataException($"Unsupported CISO version {version} (expected 1 or 2)");
        if (blockSize != BlockSize)
            throw new InvalidDataException($"Unsupported CISO block size {blockSize} (expected 2048)");

        var totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
        var indexLen = totalBlocks + 1;
        if (indexLen * 4 > source.Length - HeaderSize)
            throw new InvalidDataException("CISO index table exceeds file size");

        var indexEntries = new uint[indexLen];
        Span<byte> leBuf = stackalloc byte[4];
        // Index starts at 24
        for (long i = 0; i < indexLen; i++)
        {
            ReadExact(source, leBuf);
            indexEntries[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }

        // Decompress each sector
        var blockBuf = new byte[BlockSize];
        long written = 0;

        progress?.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: totalBlocks));

        for (long sector = 0; sector < totalBlocks; sector++)
        {
            ct.ThrowIfCancellationRequested();

            var rawEntry = indexEntries[sector];
            var rawNext = indexEntries[sector + 1];
            bool isPlain;
            if (version == CisoWriter.VersionDeflate)
            {
                // classic: high bit = plain
                isPlain = (rawEntry & 0x80000000u) != 0;
            }
            else // version 2 LZ4
            {
                // rust: high bit = compressed
                isPlain = (rawEntry & 0x80000000u) == 0;
            }

            var offset = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << align);
            var nextOffset = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << align);
            if (nextOffset < offset)
            {
                throw new InvalidDataException(
                    $"CISO index corruption at sector {sector}: next {nextOffset} < offset {offset}");
            }

            var dataLen = (long)(nextOffset - offset);
            if (dataLen < 0) throw new InvalidDataException($"Negative data length at sector {sector}");

            source.Seek((long)offset, SeekOrigin.Begin);

            // The final index entry stores position >> align, rounding down, so the last
            // compressed block's gap can be up to (1 << align) - 1 bytes short of the true
            // payload. The real bytes are present in the file — extend the read for the
            // last sector (ciso read.rs instead pads with zeros, which corrupts payloads
            // whose tail is non-zero).
            var readLen = dataLen;
            if (!isPlain && sector == totalBlocks - 1)
                readLen = Math.Min(dataLen + (1L << align) - 1, source.Length - (long)offset);

            if (isPlain)
            {
                // Plain block: read BlockSize bytes directly (ignore dataLen which may include alignment pad)
                // But ensure we don't read beyond nextOffset if pad present; plain size is BlockSize.
                long toRead = BlockSize;
                // Clamp to remaining uncompressed size for last block
                if (written + toRead > (long)uncompressedSize)
                    toRead = (long)uncompressedSize - written;

                var read = 0;
                while (read < toRead)
                {
                    var n = source.Read(blockBuf, read, (int)(toRead - read));
                    if (n == 0) throw new EndOfStreamException($"Unexpected EOF at plain sector {sector}");
                    read += n;
                }

                dest.Write(blockBuf, 0, (int)toRead);
                written += toRead;
            }
            else
            {
                // Compressed: payload at offset; may include trailing alignment pad for
                // non-final sectors, and the final sector's gap may round down (see above).
                if (readLen <= 0)
                    throw new InvalidDataException($"Zero-length compressed sector {sector}");

                var compBuf = new byte[readLen];
                var compRead = 0;
                while (compRead < readLen)
                {
                    var n = source.Read(compBuf, compRead, (int)(readLen - compRead));
                    if (n == 0) break;
                    compRead += n;
                }

                if (compRead < readLen)
                    Array.Resize(ref compBuf, compRead);

                var expected = (int)Math.Min(BlockSize, (long)uncompressedSize - written);
                var sectorData = DecompressSector(version, align, compBuf, expected);

                dest.Write(sectorData, 0, expected);
                written += expected;
            }

            progress?.Report(new ProgressInfo(ProgressInfoType.FileAdded, Path: $"/sector/{sector}", Sector: sector,
                Size: BlockSize));
        }

        // Truncate/ensure dest length equals uncompressedSize
        if (dest is FileStream fs)
        {
            fs.SetLength((long)uncompressedSize);
        }
        else
        {
            // For MemoryStream etc., truncate if needed
            if (dest.Length != (long)uncompressedSize)
            {
                try
                {
                    dest.SetLength((long)uncompressedSize);
                }
                catch
                {
                    // ignored
                }
            }
        }

        progress?.Report(new ProgressInfo(ProgressInfoType.FinishedPacking));
    }

    /// <summary>
    /// Random-access read from a CISO file, single or split (<c>*.1.cso</c> parts), decompressing
    /// the sector(s) containing the requested range.
    /// </summary>
    public static void ReadFromCso(string csoPath, long offset, Span<byte> buffer)
    {
        using var fs = OpenReadStream(csoPath);
        ReadFromCsoCore(fs, offset, buffer);
    }

    /// <summary>
    /// Random-access read from an open CISO file stream (decompresses the sector(s) containing the requested range).
    /// </summary>
    /// <param name="csoFs">Open CISO file stream (seekable, readable).</param>
    /// <param name="offset">Byte offset in the uncompressed image.</param>
    /// <param name="buffer">Destination buffer to fill.</param>
    public static void ReadFromCso(FileStream csoFs, long offset, Span<byte> buffer)
    {
        ReadFromCsoCore(csoFs, offset, buffer);
    }

    private static void ReadFromCsoCore(Stream csoFs, long offset, Span<byte> buffer)
    {
        // Minimal random-access: decompress whole needed sectors
        Span<byte> header = stackalloc byte[24];
        csoFs.Seek(0, SeekOrigin.Begin);
        ReadExact(csoFs, header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var hsize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        var version = header[20];
        var align = header[21];
        if (magic != Magic || hsize != HeaderSize || blockSize != BlockSize)
            throw new InvalidDataException("Invalid CISO header");

        var totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
        var indexLen = totalBlocks + 1;
        var indexEntries = new uint[indexLen];
        Span<byte> leBuf = stackalloc byte[4];
        for (long i = 0; i < indexLen; i++)
        {
            ReadExact(csoFs, leBuf);
            indexEntries[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }

        long bufferPos = 0;
        long remaining = buffer.Length;
        var currentOffset = offset;

        while (remaining > 0)
        {
            var sector = currentOffset / BlockSize;
            var sectorOffset = currentOffset % BlockSize;
            if (sector >= totalBlocks) throw new ArgumentOutOfRangeException(nameof(offset));

            var rawEntry = indexEntries[sector];
            var rawNext = indexEntries[sector + 1];
            var isPlain = version == CisoWriter.VersionDeflate
                ? (rawEntry & 0x80000000u) != 0
                : (rawEntry & 0x80000000u) == 0;

            var off = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << align);
            var nextOff = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << align);
            var dataLen = (long)(nextOff - off);
            csoFs.Seek((long)off, SeekOrigin.Begin);

            var sectorData = new byte[BlockSize];
            if (isPlain)
            {
                // Read plain sector
                var n = 0;
                while (n < BlockSize)
                {
                    var r = csoFs.Read(sectorData, n, BlockSize - n);
                    if (r == 0) throw new EndOfStreamException();
                    n += r;
                }
            }
            else
            {
                if (dataLen <= 0) throw new InvalidDataException($"Zero-length compressed sector {sector}");

                // The last sector's index gap can round down (final entry stores
                // position >> align); extend the read to recover the true payload.
                var readLen = dataLen;
                if (sector == totalBlocks - 1)
                    readLen = Math.Min(dataLen + (1L << align) - 1, csoFs.Length - (long)off);

                var compBuf = new byte[readLen];
                var n = 0;
                while (n < readLen)
                {
                    var r = csoFs.Read(compBuf, n, (int)(readLen - n));
                    if (r == 0) break;
                    n += r;
                }

                if (n < readLen)
                    Array.Resize(ref compBuf, n);

                sectorData = DecompressSector(version, align, compBuf, BlockSize);
            }

            var toCopy = Math.Min(remaining, BlockSize - sectorOffset);
            sectorData.AsSpan((int)sectorOffset, (int)toCopy).CopyTo(buffer.Slice((int)bufferPos, (int)toCopy));
            bufferPos += toCopy;
            remaining -= toCopy;
            currentOffset += toCopy;
        }
    }

    /// <summary>
    /// Opens a CISO source for reading: a plain <c>.cso</c> file or, for a split path
    /// (<c>*.1.cso</c>), a composite stream over all <c>*.N.cso</c> parts.
    /// </summary>
    private static Stream OpenReadStream(string path)
    {
        if (CisoSplitFile.IsSplitPath(path))
        {
            var parts = CisoSplitFile.OpenParts(path);
            if (parts.Count == 0) throw new FileNotFoundException($"CSO not found: {path}");
            return parts.Count == 1 ? parts[0] : new CisoSplitInputStream(parts);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
    }

    /// <summary>
    /// Decodes one CISO sector payload to exactly <paramref name="expected"/> bytes.
    /// Version 2 first tries the strict <c>[u32 LE block info][block data]</c> framing;
    /// both versions then fall back to alignment-trim + codec attempts for robustness
    /// against non-conforming writers.
    /// </summary>
    internal static byte[] DecompressSector(byte version, byte align, byte[] payload, int expected)
    {
        if (version == CisoWriter.VersionLz4)
        {
            var strict = TryDecodeV2Payload(payload, expected);
            if (strict != null) return strict;
        }

        // Lenient fallbacks: trailing zero bytes may be alignment padding, so try
        // trimming them before decoding.
        var maxTrim = Math.Max(align == 0 ? 0 : (1 << align) - 1, 3);
        for (var trim = 0; trim <= maxTrim && trim <= payload.Length; trim++)
        {
            var tryLen = payload.Length - trim;
            if (tryLen <= 0) continue;
            var tailZero = true;
            for (var z = tryLen; z < payload.Length; z++)
            {
                if (payload[z] != 0)
                {
                    tailZero = false;
                    break;
                }
            }

            if (!tailZero && trim != 0) continue;

            var decoded = TryDecodeAny(version, payload.AsSpan(0, tryLen), expected);
            if (decoded != null) return decoded;
        }

        var full = TryDecodeAny(version, payload, expected);
        if (full != null) return full;

        throw new InvalidDataException(
            $"Failed to decompress CISO sector ({payload.Length}-byte payload, version {version}, expected {expected} bytes)");
    }

    /// <summary>
    /// Strict CISO v2 payload decode: the payload is the sector's LZ4 frame with the 7-byte
    /// frame header and 4-byte end mark stripped — <c>[u32 LE block info][block data]</c> where
    /// the block-info high bit marks an uncompressed (raw) block. This mirrors
    /// <c>ciso read.rs</c>, which re-adds the <c>LZ4_HEADER</c> before handing the data to the
    /// frame decoder.
    /// </summary>
    private static byte[]? TryDecodeV2Payload(byte[] payload, int expected)
    {
        if (payload.Length < 4) return null;
        var sizeField = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
        var blockLen = (int)(sizeField & 0x7FFFFFFFu);
        if (blockLen <= 0 || blockLen > payload.Length - 4) return null;

        var body = payload.AsSpan(4, blockLen);
        if ((sizeField & 0x80000000u) != 0)
        {
            // The frame stored the sector uncompressed.
            return body.Length >= expected ? body.Slice(0, expected).ToArray() : null;
        }

        var dst = new byte[BlockSize];
        try
        {
            var n = Lz4.Decompress(body, dst);
            // Accept an exact remainder or a full (zero-padded) last block.
            if (n == expected || (n == BlockSize && expected < BlockSize))
                return dst[..expected];
        }
        catch
        {
            // fall through to lenient paths
        }

        return null;
    }

    /// <summary>
    /// Sector payload codec tried during CISO decompression. The declared version is tried
    /// first, then the other codec as a cross-compatibility fallback.
    /// </summary>
    private enum SectorCodec
    {
        /// <summary>LZ4 sector payload (CISO version 2).</summary>
        Lz4,

        /// <summary>Raw DEFLATE sector payload (CISO version 1).</summary>
        Deflate
    }

    private static byte[]? TryDecodeAny(byte version, ReadOnlySpan<byte> data, int expected)
    {
        // Declared codec first, then the cross-codec fallback (e.g. DEFLATE bytes under a v2 header).
        var primary = version == CisoWriter.VersionLz4 ? SectorCodec.Lz4 : SectorCodec.Deflate;
        var secondary = primary == SectorCodec.Lz4 ? SectorCodec.Deflate : SectorCodec.Lz4;
        return TryDecode(primary, data, expected) ?? TryDecode(secondary, data, expected);
    }

    private static byte[]? TryDecode(SectorCodec codec, ReadOnlySpan<byte> data, int expected)
    {
        byte[] decoded;
        try
        {
            decoded = codec == SectorCodec.Lz4 ? DecodeLz4(data) : DecodeDeflate(data);
        }
        catch
        {
            return null;
        }

        // Accept an exact match, or a full zero-padded last block truncated to the remainder.
        if (decoded.Length == expected) return decoded;
        if (decoded.Length == BlockSize && expected < BlockSize) return decoded[..expected];
        return null;
    }

    private static byte[] DecodeLz4(ReadOnlySpan<byte> data)
    {
        var dst = new byte[BlockSize];
        var n = Lz4.Decompress(data, dst);
        return dst[..n];
    }

    private static byte[] DecodeDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var n = s.Read(buf[offset..]);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    /// <summary>
    /// Derives the default <c>.iso</c> output path for <paramref name="csoPath"/>
    /// (same rule <see cref="DecompressToIso"/> uses when no explicit output is given):
    /// the <c>.cso</c>/<c>.1.cso</c> suffix is replaced with <c>.iso</c>.
    /// </summary>
    public static string DeriveDefaultIsoPath(string csoPath)
    {
        var dir = Path.GetDirectoryName(csoPath) ?? "";
        var file = Path.GetFileName(csoPath);
        // Strip .cso and restore .iso
        if (file.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^".1.cso".Length] + ".iso";
        }
        else if (file.EndsWith(".cso", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^4] + ".iso";
        }
        else
        {
            var ext = Path.GetExtension(file);
            if (!string.IsNullOrEmpty(ext))
                file = Path.ChangeExtension(file, ".iso");
            else
                file += ".iso";
        }

        return Path.Combine(dir, file);
    }
}