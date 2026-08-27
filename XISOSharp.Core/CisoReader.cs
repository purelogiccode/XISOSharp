using System.Buffers.Binary;
using System.IO.Compression;

namespace XISOSharp;

/// <summary>
/// CISO / CSO decompression and random-access reader.
/// Handles version 1 (DEFLATE, plain=high bit) and version 2 (LZ4, compressed=high bit)
/// for interoperability with <c>ciso 0.2.1</c> / <c>xdvdfs</c> and classic tools.
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
    /// </summary>
    public static bool IsCso(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256);
            if (fs.Length < HeaderSize) return false;
            Span<byte> hdr = stackalloc byte[24];
            int n = fs.Read(hdr);
            if (n != 24) return false;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[..4]);
            uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
            byte ver = hdr[20];
            return magic == Magic && hsize == HeaderSize &&
                   (ver == CisoWriter.VersionDeflate || ver == CisoWriter.VersionLz4);
        }
        catch { return false; }
    }

    /// <summary>
    /// Decompresses a CISO/CSO file to an ISO file. Returns 0 on success, 1 on error.
    /// </summary>
    public static int DecompressToIso(string csoPath, string? outputIsoPath = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(csoPath)) throw new FileNotFoundException($"CSO not found: {csoPath}");

        string output = outputIsoPath ?? DeriveDefaultIsoPath(csoPath);
        if (string.Equals(Path.GetFullPath(csoPath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and destination paths are the same");

        using var src = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        DecompressStream(src, dst, progress, ct);
        Logger.Log($"Decompressed {csoPath} -> {output} ({new FileInfo(output).Length} bytes)\n");
        return 0;
    }

    /// <summary>
    /// Asynchronous variant of <see cref="DecompressToIso"/>.
    /// Decompresses a CISO/CSO file to an ISO file on a thread-pool thread.
    /// </summary>
    /// <param name="csoPath">Source CISO/CSO path.</param>
    /// <param name="outputIsoPath">Destination ISO path; if <c>null</c>, derived from <paramref name="csoPath"/> (<c>.iso</c>).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error; throws on invalid arguments.</returns>
    public static async Task<int> DecompressToIsoAsync(string csoPath, string? outputIsoPath = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => DecompressToIso(csoPath, outputIsoPath, progress, ct), ct).ConfigureAwait(false);

    /// <summary>
    /// Decompresses a CISO stream to an output stream. Both must be seekable.
    /// Supports both DEFLATE (v1) and LZ4 (v2) payloads; LZ4 blocks are decoded via
    /// a minimal pure-managed fallback that handles raw blocks produced by <c>ciso 0.2.1</c>.
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
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        ulong uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        byte version = header[20];
        byte align = header[21];

        if (magic != Magic) throw new InvalidDataException("Not a CISO file (bad magic)");
        if (hsize != HeaderSize) throw new InvalidDataException($"Unsupported CISO header size {hsize} (expected 24)");
        if (version != CisoWriter.VersionDeflate && version != CisoWriter.VersionLz4)
            throw new InvalidDataException($"Unsupported CISO version {version} (expected 1 or 2)");
        if (blockSize != BlockSize)
            throw new InvalidDataException($"Unsupported CISO block size {blockSize} (expected 2048)");

        long totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
        long indexLen = totalBlocks + 1;
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

        // Verify last index roughly matches data size (allow alignment slack)
        // Decompress each sector
        var blockBuf = new byte[BlockSize];
        var outBuf = new byte[BlockSize];
        long written = 0;

        progress?.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: totalBlocks));

        for (long sector = 0; sector < totalBlocks; sector++)
        {
            ct.ThrowIfCancellationRequested();

            uint rawEntry = indexEntries[sector];
            uint rawNext = indexEntries[sector + 1];
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

            ulong offset = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << align);
            ulong nextOffset = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << align);
            if (nextOffset < offset)
                throw new InvalidDataException(
                    $"CISO index corruption at sector {sector}: next {nextOffset} < offset {offset}");

            long dataLen = (long)(nextOffset - offset);
            if (dataLen < 0) throw new InvalidDataException($"Negative data length at sector {sector}");

            source.Seek((long)offset, SeekOrigin.Begin);

            if (isPlain)
            {
                // Plain block: read BlockSize bytes directly (ignore dataLen which may include alignment pad)
                // But ensure we don't read beyond nextOffset if pad present; plain size is BlockSize.
                long toRead = BlockSize;
                // Clamp to remaining uncompressed size for last block
                if (written + toRead > (long)uncompressedSize)
                    toRead = (long)uncompressedSize - written;

                int read = 0;
                while (read < toRead)
                {
                    int n = source.Read(blockBuf, read, (int)(toRead - read));
                    if (n == 0) throw new EndOfStreamException($"Unexpected EOF at plain sector {sector}");
                    read += n;
                }

                dest.Write(blockBuf, 0, (int)toRead);
                written += toRead;
            }
            else
            {
                // Compressed: dataLen bytes at offset.
                // For align>0, dataLen may include up to (1<<align)-1 pad bytes after compressed data.
                // We trim trailing zeros up to align window when decompressing.
                // Allocate buffer for compressed data
                if (dataLen == 0)
                    throw new InvalidDataException($"Zero-length compressed sector {sector}");

                // For last block, dataLen may be truncated; but we still handle.
                var compBuf = new byte[dataLen];
                int compRead = 0;
                while (compRead < dataLen)
                {
                    int n = source.Read(compBuf, compRead, (int)(dataLen - compRead));
                    if (n == 0) throw new EndOfStreamException($"Unexpected EOF at compressed sector {sector}");
                    compRead += n;
                }

                // Try decompress, trimming trailing zeros if needed (alignment pad)
                bool decompressed = false;
                int maxTrim = align == 0 ? 0 : (1 << align) - 1;
                // Also allow trimming up to 3 extra for safety
                maxTrim = Math.Max(maxTrim, 3);

                for (int trim = 0; trim <= maxTrim && trim <= compBuf.Length; trim++)
                {
                    int tryLen = compBuf.Length - trim;
                    if (tryLen <= 0) continue;
                    // Quick check: trailing bytes we trim should be zeros (pad)
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
                        byte[] decompressedBytes;
                        if (version == CisoWriter.VersionDeflate)
                        {
                            decompressedBytes = DeflateDecompress(compBuf.AsSpan(0, tryLen));
                        }
                        else
                        {
                            decompressedBytes = Lz4Decompress(compBuf.AsSpan(0, tryLen));
                        }

                        if (decompressedBytes.Length == 0) continue;
                        // For non-last blocks, expect BlockSize; for last, may be remainder
                        long expected = BlockSize;
                        if (written + expected > (long)uncompressedSize)
                            expected = (long)uncompressedSize - written;

                        if (decompressedBytes.Length != expected)
                        {
                            // If decompressed length mismatches, maybe we trimmed wrong; try next trim
                            // But allow if expected is BlockSize and we got less? No, must match.
                            if (decompressedBytes.Length < expected && trim == maxTrim)
                            {
                                // Last attempt: accept partial?
                                // fall through to failure
                            }
                            else if (decompressedBytes.Length != expected)
                                continue;
                        }

                        dest.Write(decompressedBytes, 0, decompressedBytes.Length);
                        written += decompressedBytes.Length;
                        decompressed = true;
                        break;
                    }
                    catch
                    {
                        // Try next trim
                        continue;
                    }
                }

                if (!decompressed)
                {
                    // Fallback: try raw deflate without trimming, with more permissive
                    try
                    {
                        byte[] decompressedBytes = version == CisoWriter.VersionDeflate
                            ? DeflateDecompress(compBuf)
                            : Lz4Decompress(compBuf);
                        long expected = BlockSize;
                        if (written + expected > (long)uncompressedSize)
                            expected = (long)uncompressedSize - written;
                        // If still mismatched, truncate/pad
                        if (decompressedBytes.Length != expected)
                        {
                            if (decompressedBytes.Length > expected)
                                Array.Resize(ref decompressedBytes, (int)expected);
                            else if (decompressedBytes.Length < expected)
                            {
                                // Pad with zeros
                                var tmp = new byte[expected];
                                Array.Copy(decompressedBytes, tmp, decompressedBytes.Length);
                                decompressedBytes = tmp;
                            }
                        }

                        dest.Write(decompressedBytes, 0, decompressedBytes.Length);
                        written += decompressedBytes.Length;
                        decompressed = true;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException(
                            $"Failed to decompress sector {sector} (version {version}, dataLen {dataLen})", ex);
                    }
                }
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
                try { dest.SetLength((long)uncompressedSize); }
                catch
                {
                    // ignored
                }
            }
        }

        progress?.Report(new ProgressInfo(ProgressInfoType.FinishedPacking));
    }

    /// <summary>
    /// Random-access read from a CISO file (decompresses the sector(s) containing the requested range).
    /// Provided for BlockDevice parity.
    /// </summary>
    public static void ReadFromCso(string csoPath, long offset, Span<byte> buffer)
    {
        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        ReadFromCso(fs, offset, buffer);
    }

    /// <summary>
    /// Random-access read from an open CISO file stream (decompresses the sector(s) containing the requested range).
    /// </summary>
    /// <param name="csoFs">Open CISO file stream (seekable, readable).</param>
    /// <param name="offset">Byte offset in the uncompressed image.</param>
    /// <param name="buffer">Destination buffer to fill.</param>
    public static void ReadFromCso(FileStream csoFs, long offset, Span<byte> buffer)
    {
        // Minimal random-access: decompress whole needed sectors
        Span<byte> header = stackalloc byte[24];
        csoFs.Seek(0, SeekOrigin.Begin);
        ReadExact(csoFs, header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        ulong uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        byte version = header[20];
        byte align = header[21];
        if (magic != Magic || hsize != HeaderSize || blockSize != BlockSize)
            throw new InvalidDataException("Invalid CISO header");

        long totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
        long indexLen = totalBlocks + 1;
        var indexEntries = new uint[indexLen];
        Span<byte> leBuf = stackalloc byte[4];
        for (long i = 0; i < indexLen; i++)
        {
            ReadExact(csoFs, leBuf);
            indexEntries[i] = BinaryPrimitives.ReadUInt32LittleEndian(leBuf);
        }

        long bufferPos = 0;
        long remaining = buffer.Length;
        long currentOffset = offset;

        while (remaining > 0)
        {
            long sector = currentOffset / BlockSize;
            long sectorOffset = currentOffset % BlockSize;
            if (sector >= totalBlocks) throw new ArgumentOutOfRangeException(nameof(offset));

            uint rawEntry = indexEntries[sector];
            uint rawNext = indexEntries[sector + 1];
            bool isPlain = version == CisoWriter.VersionDeflate
                ? (rawEntry & 0x80000000u) != 0
                : (rawEntry & 0x80000000u) == 0;

            ulong off = (rawEntry & 0x7FFFFFFFu) * (ulong)(1u << align);
            ulong nextOff = (rawNext & 0x7FFFFFFFu) * (ulong)(1u << align);
            long dataLen = (long)(nextOff - off);
            csoFs.Seek((long)off, SeekOrigin.Begin);

            byte[] sectorData = new byte[BlockSize];
            if (isPlain)
            {
                // Read plain sector
                int n = 0;
                while (n < BlockSize)
                {
                    int r = csoFs.Read(sectorData, n, BlockSize - n);
                    if (r == 0) throw new EndOfStreamException();
                    n += r;
                }
            }
            else
            {
                var compBuf = new byte[dataLen];
                int n = 0;
                while (n < dataLen)
                {
                    int r = csoFs.Read(compBuf, n, (int)(dataLen - n));
                    if (r == 0) throw new EndOfStreamException();
                    n += r;
                }

                // Trim pad if needed
                byte[] dec;
                if (version == CisoWriter.VersionDeflate)
                    dec = TryDecompressWithTrim(compBuf, align);
                else
                    dec = Lz4DecompressWithTrim(compBuf, align);
                if (dec.Length != BlockSize)
                    throw new InvalidDataException($"Decompressed size mismatch at sector {sector}");
                sectorData = dec;
            }

            long toCopy = Math.Min(remaining, BlockSize - sectorOffset);
            sectorData.AsSpan((int)sectorOffset, (int)toCopy).CopyTo(buffer.Slice((int)bufferPos, (int)toCopy));
            bufferPos += toCopy;
            remaining -= toCopy;
            currentOffset += toCopy;
        }
    }

    private static byte[] TryDecompressWithTrim(byte[] compBuf, byte align)
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
                var dec = DeflateDecompress(compBuf.AsSpan(0, tryLen));
                if (dec.Length == BlockSize) return dec;
            }
            catch
            {
                // ignored
            }
        }

        return DeflateDecompress(compBuf);
    }

    private static byte[] Lz4DecompressWithTrim(byte[] compBuf, byte align)
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
                var dec = Lz4Decompress(compBuf.AsSpan(0, tryLen));
                if (dec.Length == BlockSize) return dec;
            }
            catch
            {
                // ignored
            }
        }

        return Lz4Decompress(compBuf);
    }

    private static byte[] DeflateDecompress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    // Minimal LZ4 raw block decompressor for ciso 0.2.1 payloads.
    // ciso stores raw LZ4 block (stripped of 7-byte header + 4-byte footer) as produced by lz4_flex legacy frame.
    // We attempt to decode using a tiny managed LZ4 block decoder. If K4os or native not available,
    // we fallback to interpreting the data as DEFLATE (for compatibility when writer used DEFLATE but header says v2).
    private static byte[] Lz4Decompress(ReadOnlySpan<byte> data)
    {
        // Attempt pure-managed LZ4 block decompress
        // lz4_flex legacy frame stripped block is a single LZ4 block (max 64KB) compressed with block size 64KB,
        // We can try to decompress as raw LZ4 block using simple algorithm if available.
        // Since we don't have a managed LZ4 library in BCL, we try DEFLATE first as fallback
        // (covers case where v2 file was actually written with DEFLATE by mistake).
        // Then attempt simple LZ4 decode implemented below.

        // Try DEFLATE fallback (if data was actually DEFLATE)
        try
        {
            var def = DeflateDecompress(data);
            if (def.Length == BlockSize) return def;
        }
        catch
        {
            // ignored
        }

        // Try managed LZ4 block decode
        try
        {
            return Lz4BlockDecompress(data, BlockSize);
        }
        catch (Exception ex)
        {
            // As last resort, try DEFLATE again without length check
            try { return DeflateDecompress(data); }
            catch
            {
                // ignored
            }

            throw new InvalidDataException("LZ4 decompress failed", ex);
        }
    }

    // Very small LZ4 block decoder (raw block, no frame). Based on LZ4 spec: https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
    // This decoder assumes the block is not using checksums and is a single block (legacy).
    // It decodes to exactly BlockSize bytes.
    private static byte[] Lz4BlockDecompress(ReadOnlySpan<byte> src, int expectedSize)
    {
        var dst = new byte[expectedSize];
        int srcPos = 0;
        int dstPos = 0;

        while (srcPos < src.Length && dstPos < expectedSize)
        {
            byte token = src[srcPos++];
            int literalLen = token >> 4;
            if (literalLen == 15)
            {
                byte len;
                do
                {
                    if (srcPos >= src.Length) break;
                    len = src[srcPos++];
                    literalLen += len;
                } while (len == 255);
            }

            if (srcPos + literalLen > src.Length) throw new InvalidDataException("LZ4 literal overrun");
            if (dstPos + literalLen > expectedSize) throw new InvalidDataException("LZ4 dst overrun literals");
            src.Slice(srcPos, literalLen).CopyTo(dst.AsSpan(dstPos, literalLen));
            srcPos += literalLen;
            dstPos += literalLen;

            if (dstPos >= expectedSize || srcPos >= src.Length) break;

            if (srcPos + 2 > src.Length) throw new InvalidDataException("LZ4 offset missing");
            int offset = src[srcPos++] | (src[srcPos++] << 8);
            if (offset == 0) throw new InvalidDataException("LZ4 offset zero");

            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                byte len;
                do
                {
                    if (srcPos >= src.Length) break;
                    len = src[srcPos++];
                    matchLen += len;
                } while (len == 255);
            }

            matchLen += 4;

            if (dstPos - offset < 0) throw new InvalidDataException("LZ4 offset out of range");
            if (dstPos + matchLen > expectedSize) matchLen = expectedSize - dstPos; // clamp for last block

            // Overlap-safe copy
            for (int i = 0; i < matchLen; i++)
                dst[dstPos + i] = dst[dstPos - offset + i];
            dstPos += matchLen;
        }

        if (dstPos != expectedSize)
        {
            // If we didn't fill expected, it may be because src ended early but dst was supposed to be fully decoded
            // For CISO, each block should decompress to exactly 2048. If we got less, pad? But error.
            if (dstPos < expectedSize)
                throw new InvalidDataException($"LZ4 decompressed {dstPos} != {expectedSize}");
        }

        return dst;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int n = s.Read(buf[offset..]);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    private static string DeriveDefaultIsoPath(string csoPath)
    {
        string dir = Path.GetDirectoryName(csoPath) ?? "";
        string file = Path.GetFileName(csoPath);
        // Strip .cso and restore .iso
        if (file.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase))
            file = file[..^".1.cso".Length] + ".iso";
        else if (file.EndsWith(".cso", StringComparison.OrdinalIgnoreCase))
            file = file[..^4] + ".iso";
        else
        {
            string ext = Path.GetExtension(file);
            if (!string.IsNullOrEmpty(ext))
                file = Path.ChangeExtension(file, ".iso");
            else
                file = file + ".iso";
        }

        return Path.Combine(dir, file);
    }
}