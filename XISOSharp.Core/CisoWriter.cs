using System.Buffers.Binary;
using System.IO.Compression;

namespace XISOSharp;

/// <summary>
/// CISO / CSO compressed ISO writer, ported from <c>ciso 0.2.1</c> (<c>layout.rs</c> + <c>write.rs</c>)
/// and <c>References/xdvdfs-0.8.3/xdvdfs-cli/src/cmd_compress.rs</c>.
/// Uses raw DEFLATE per 2048-byte sector (BCL <see cref="DeflateStream"/>, no native),
/// keeping <c>IsTrimmable</c>/<c>IsAotCompatible</c> true. Reader handles both
/// version 1 (DEFLATE, plain=0x80000000) and version 2 (LZ4, compressed=0x80000000)
/// for interop with <c>xdvdfs</c> (<c>lz4_flex</c>) and classic tools.
/// </summary>
public static class CisoWriter
{
    /// <summary>CISO block size in bytes (2048, matches XISO sector size).</summary>
    public const int BlockSize = 2048;

    /// <summary>CISO magic <c>0x4F534943</c> ("CISO" little-endian).</summary>
    public const uint Magic = 0x4F534943u; // "CISO" LE

    /// <summary>CISO header size in bytes (24).</summary>
    public const uint HeaderSize = 24;

    /// <summary>CISO version 1 — classic DEFLATE payload where high bit means plain.</summary>
    public const byte VersionDeflate = 1; // classic CISO with DEFLATE

    /// <summary>CISO version 2 — xdvdfs / ciso 0.2 LZ4 payload where high bit means compressed.</summary>
    public const byte VersionLz4 = 2; // xdvdfs / ciso 0.2 (LZ4)

    // Threshold mirroring ciso write.rs: only store compressed if it saves >12 bytes (7 header +4 footer +1)
    private const int CompressionSavingThreshold = 12;

    /// <summary>
    /// Compresses an XISO image or a directory (which is first packed into a temp XISO) to CISO/CSO.
    /// </summary>
    /// <param name="sourcePath">Source directory or ISO file.</param>
    /// <param name="outputCsoPath">Destination CSO path; if null, derived from source (<c>.cso</c>).</param>
    /// <param name="level">Compression level 0..9 (0=store, 1=fastest … 9=smallestSize). Default 6.</param>
    /// <param name="splitBytes">Optional split threshold; when set, output is split into <c>.1.cso</c> parts (not yet implemented — reserved).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int CompressToCso(string sourcePath, string? outputCsoPath = null, int level = 6,
        long? splitBytes = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (level < 0 || level > 9)
            throw new ArgumentOutOfRangeException(nameof(level), "CISO level must be 0..9");

        if (splitBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(splitBytes));

        // Split not yet implemented — validate but ignore for now; future work will use ciso::split semantics
        if (splitBytes.HasValue)
            Logger.LogErr("warning: --ciso-split is reserved and currently ignored (single-file output)\n");

        bool isDir = Directory.Exists(sourcePath);
        bool isFile = File.Exists(sourcePath);
        if (!isDir && !isFile)
            throw new FileNotFoundException($"Source not found: {sourcePath}");

        string output = outputCsoPath ?? DeriveDefaultCsoPath(sourcePath, isDir);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and destination paths are the same");

        string? tempIso = null;
        string sourceFile;

        if (isDir)
        {
            // Pack directory to temp XISO then compress. Mirrors xdvdfs cmd_compress path for is_dir.
            tempIso = Path.Combine(Path.GetTempPath(), $"xisosh-temp-{Guid.NewGuid():N}.iso");
            // Use PackFromDirectory (1:1 mapping). Exclude none.
            int rc = XisoWriterInternal.PackFromDirectoryForCiso(sourcePath, tempIso, ct);
            if (rc != 0)
                throw new IOException($"Failed to pack directory {sourcePath} to temp ISO");
            sourceFile = tempIso;
        }
        else
        {
            sourceFile = sourcePath;
        }

        try
        {
            using var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            CompressStream(src, dst, level, progress, ct);

            // Ensure split handling future: close dst before returning
            Logger.Log($"Compressed {sourcePath} -> {output} ({new FileInfo(output).Length} bytes)\n");
            return 0;
        }
        finally
        {
            if (tempIso != null)
            {
                try { File.Delete(tempIso); }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>Asynchronous variant of <see cref="CompressToCso"/>.</summary>
    public static async Task<int> CompressToCsoAsync(string sourcePath, string? outputCsoPath = null, int level = 6,
        long? splitBytes = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => CompressToCso(sourcePath, outputCsoPath, level, splitBytes, progress, ct), ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Compresses a seekable source stream (uncompressed ISO) to a seekable destination stream (CISO).
    /// Source is read sector-wise (2048 bytes) and written as CISO header + index + deflated blocks.
    /// </summary>
    public static void CompressStream(Stream source, Stream dest, int level = 6,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!source.CanSeek) throw new ArgumentException("Source must be seekable", nameof(source));
        if (!dest.CanSeek) throw new ArgumentException("Destination must be seekable", nameof(dest));

        long uncompressedSize = source.Length;
        // Also handle non-zero Position: we want whole stream
        // Ensure we start from 0
        source.Seek(0, SeekOrigin.Begin);
        dest.Seek(0, SeekOrigin.Begin);

        int totalBlocks = (int)((uncompressedSize + BlockSize - 1) / BlockSize);
        int indexLen = totalBlocks + 1;

        // Dynamic alignment: mirror Python logic and rust's fixed 2 for large images.
        // Keep align=0 for <2GB to avoid padding overhead; align=1 for <4GB; align=2 for >=4GB.
        int align;
        if (uncompressedSize < 0x80000000L) align = 0;
        else if (uncompressedSize < 0x100000000L) align = 1;
        else align = 2;

        byte version = VersionDeflate;
        // If we ever switch to LZ4, version = VersionLz4 and align = 2 (rust default).

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), (ulong)uncompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), BlockSize);
        header[20] = version;
        header[21] = (byte)align;
        header[22] = 0;
        header[23] = 0;

        dest.Write(header, 0, header.Length);

        // Reserve index table (filled later)
        long indexStart = dest.Position;
        var indexBytes = new byte[indexLen * 4];
        dest.Write(indexBytes, 0, indexBytes.Length);

        long dataStart = HeaderSize + indexLen * 4L;
        long position = dataStart;

        uint[] indexEntries = new uint[indexLen];

        var blockBuf = new byte[BlockSize];
        // For progress
        progress?.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: totalBlocks));

        CompressionLevel compLevel = MapLevel(level);

        for (int sector = 0; sector < totalBlocks; sector++)
        {
            ct.ThrowIfCancellationRequested();

            // Align position before this block
            if (align != 0)
            {
                long alignBytes = 1L << align;
                long mis = position & (alignBytes - 1);
                if (mis != 0)
                {
                    long pad = alignBytes - mis;
                    var padBuf = new byte[pad];
                    dest.Seek(position, SeekOrigin.Begin);
                    dest.Write(padBuf, 0, padBuf.Length);
                    position += pad;
                }
            }

            // Read one block (pad last block with zeros if file not multiple of BlockSize)
            int read = 0;
            while (read < BlockSize)
            {
                int n = source.Read(blockBuf, read, BlockSize - read);
                if (n == 0) break;
                read += n;
            }

            if (read < BlockSize)
                Array.Clear(blockBuf, read, BlockSize - read);

            // Compress
            byte[] compressed;
            if (level == 0)
            {
                compressed = Array.Empty<byte>();
            }
            else
            {
                compressed = DeflateCompress(blockBuf, compLevel);
            }

            bool usePlain;
            byte[] dataToWrite;
            if (level == 0 || compressed.Length == 0 || compressed.Length + CompressionSavingThreshold >= BlockSize)
            {
                // Store plain (no saving)
                usePlain = true;
                dataToWrite = blockBuf;
            }
            else
            {
                usePlain = false;
                dataToWrite = compressed;
            }

            uint posShifted = (uint)(position >> align);
            uint entry = posShifted & 0x7FFFFFFFu;
            if (usePlain)
                entry |= 0x80000000u; // classic: high bit = plain
            // For version 2 LZ4, this would be opposite: entry |= isCompressed ? 0x80000000 : 0

            indexEntries[sector] = entry;

            dest.Seek(position, SeekOrigin.Begin);
            dest.Write(dataToWrite, 0, dataToWrite.Length);
            position += dataToWrite.Length;

            // Progress: report file added? Use Sector
            progress?.Report(new ProgressInfo(ProgressInfoType.FileAdded, Path: $"/sector/{sector}", Sector: sector,
                Size: dataToWrite.Length));
        }

        // Final index entry (end of file) — never plain
        {
            // Align final position? No need to align final, but store as is (no pad after last block)
            uint posShifted = (uint)(position >> align);
            indexEntries[indexLen - 1] = posShifted & 0x7FFFFFFFu;
        }

        // Write index table at indexStart
        dest.Seek(indexStart, SeekOrigin.Begin);
        Span<byte> leBuf = stackalloc byte[4];
        foreach (uint e in indexEntries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(leBuf, e);
            dest.Write(leBuf);
        }

        // Seek to end
        dest.Seek(position, SeekOrigin.Begin);
        progress?.Report(new ProgressInfo(ProgressInfoType.FinishedPacking));
    }

    private static byte[] DeflateCompress(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        // Use optimal leaveOpen false; but we need to dispose DeflateStream before ToArray
        using (var ds = new DeflateStream(ms, level, leaveOpen: true))
        {
            ds.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    private static CompressionLevel MapLevel(int level)
        => level switch
        {
            0 => CompressionLevel.NoCompression,
            1 or 2 => CompressionLevel.Fastest,
            3 or 4 or 5 or 6 or 7 => CompressionLevel.Optimal,
            8 or 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

    private static string DeriveDefaultCsoPath(string sourcePath, bool isDir)
    {
        if (isDir)
        {
            string trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed) ?? Directory.GetCurrentDirectory();
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "image";
            return Path.Combine(parent, name + ".cso");
        }
        else
        {
            // For file, replace extension with .cso (or append if no ext)
            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string file = Path.GetFileName(sourcePath);
            // If file ends with .iso, produce .cso sibling
            if (file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                file = file[..^4] + ".cso";
            else if (file.EndsWith(".xiso", StringComparison.OrdinalIgnoreCase))
                file = file[..^5] + ".cso";
            else
            {
                string ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext))
                    file = Path.ChangeExtension(file, ".cso");
                else
                    file = file + ".cso";
            }

            return Path.Combine(dir, file);
        }
    }

    // Internal helper to avoid recursion with public CompressToCso calling PackFromDirectory which might call Compress again
    private static class XisoWriterInternal
    {
        public static int PackFromDirectoryForCiso(string sourceDirectory, string outputIsoPath, CancellationToken ct)
        {
            // Use XisoWriter.CreateXiso directly with explicit output name to avoid extra .iso
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputIsoPath)) ?? Directory.GetCurrentDirectory();
            string name = Path.GetFileName(outputIsoPath);
            // name includes .iso already (temp file)
            Directory.CreateDirectory(dir);
            // CreateXiso expects rootDirectory + outputDirectory + inName; we want exact path
            // Use PackFromDirectory with outputIsoPath
            return XisoWriter.PackFromDirectory(sourceDirectory, outputIsoPath, cancellationToken: ct);
        }
    }
}