using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// CISO / CSO compressed ISO writer, ported from <c>ciso 0.2.1</c> (<c>layout.rs</c> + <c>write.rs</c> + <c>split.rs</c>)
/// and <c>References/xdvdfs-0.8.3/xdvdfs-cli/src/cmd_compress.rs</c>.
/// Version 1 emits raw DEFLATE per 2048-byte sector (BCL <see cref="DeflateStream"/>, no native);
/// version 2 emits LZ4 sector payloads (via <see cref="Lz4"/>, byte-identical to the <c>lz4_flex</c>
/// encoder used by <c>ciso 0.2</c>) with the fixed <c>align 2</c> the reference writes — matching what
/// modern <c>xdvdfs compress</c> produces. Output can be split into <c>.1.cso</c>/<c>.2.cso</c> parts
/// through <see cref="CisoSplitOutput"/> (<c>ciso::split::SplitOutput</c> parity). Both versions keep
/// <c>IsTrimmable</c>/<c>IsAotCompatible</c> true; the reader handles both.
/// </summary>
public static class CisoWriter
{
    /// <summary>CISO block size in bytes (2048, matches XISO sector size).</summary>
    public const int BlockSize = 2048;

    /// <summary>CISO magic <c>0x4F534943</c> ("CISO" little-endian).</summary>
    public const uint Magic = 0x4F534943u; // "CISO" LE

    /// <summary>CISO header size in bytes (24).</summary>
    public const uint HeaderSize = 24;

    /// <summary>
    /// Maximum accepted <c>jobs</c>/<c>--jobs</c> value for parallel sector
    /// compression. Matches the CLI batch ceiling so one documented limit
    /// covers both forms of parallelism.
    /// </summary>
    public const int MaxParallelism = 64;

    /// <summary>CISO version 1 — classic DEFLATE payload where high bit means plain.</summary>
    public const byte VersionDeflate = 1; // classic CISO with DEFLATE

    /// <summary>CISO version 2 — xdvdfs / ciso 0.2 LZ4 payload where high bit means compressed.</summary>
    public const byte VersionLz4 = 2; // xdvdfs / ciso 0.2 (LZ4)

    /// <summary>
    /// Default split point for split output (<c>ciso::split::FILE_SPLIT_POINT</c>, ~4 GiB) —
    /// the threshold modern <c>xdvdfs compress</c> always writes through. This is
    /// a compression-format default, not a FATX-safe limit: it stays below the
    /// 32-bit file-size field but above <see cref="Constants.FatxMaxFileSize"/>.
    /// </summary>
    public const long DefaultSplitPoint = 0xffbf6000;

    // Threshold mirroring ciso write.rs: only store compressed if it saves >12 bytes (7 header +4 footer +1)
    private const int CompressionSavingThreshold = 12;

    /// <summary>
    /// Compresses an XISO image or a directory (which is first packed into a temp XISO) to CISO/CSO.
    /// Split ISO, CISO, CCI and GOD sources are read through the seekable decompressed view from
    /// <see cref="XisoImageSource"/> and compressed directly, without a joined temporary ISO.
    /// </summary>
    /// <param name="sourcePath">Source directory, ISO file, split ISO/CISO/CCI image or GOD container.</param>
    /// <param name="outputCsoPath">Destination CSO path; if null, derived from source (<c>.cso</c>).</param>
    /// <param name="level">Compression level 0..9 (0=store, 1=fastest … 9=smallestSize). Default 6.
    /// For version 2 the level maps inversely to the LZ4 acceleration parameter
    /// (<c>acceleration = 10 - level</c>; level 9 is byte-identical to <c>xdvdfs compress</c>).</param>
    /// <param name="splitBytes">
    /// Optional split threshold. When set, output is written as <c>&lt;name&gt;.1.cso</c>,
    /// <c>&lt;name&gt;.2.cso</c>, … parts (ciso <c>SplitOutput</c> semantics; see
    /// <see cref="DefaultSplitPoint"/> for the value xdvdfs uses). When null, a single
    /// <c>.cso</c> file is written.
    /// </param>
    /// <param name="version">CISO version: <see cref="VersionLz4"/> (default, matches modern xdvdfs)
    /// or <see cref="VersionDeflate"/> (classic).</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int CompressToCso(string sourcePath, string? outputCsoPath = null, int level = 6,
        long? splitBytes = null, byte version = VersionLz4,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        CompressToCso(sourcePath, outputCsoPath, level, splitBytes, version, 1, progress, ct);

    /// <summary>
    /// Bounded-parallel variant of
    /// <see cref="CompressToCso(string, string?, int, long?, byte, IProgress{ProgressInfo}?, CancellationToken)"/>:
    /// sectors are compressed by up to <paramref name="jobs"/> workers and
    /// written back in sector order, so the output is byte-identical to the
    /// single-threaded path. Only CISO v2/v1 compression benefits; level
    /// <c>0</c> (store) and <c>jobs</c> = 1 keep the sequential writer.
    /// </summary>
    /// <param name="sourcePath">Source directory, ISO file, split ISO/CISO/CCI image or GOD container.</param>
    /// <param name="outputCsoPath">Destination CSO path; if null, derived from source (<c>.cso</c>).</param>
    /// <param name="level">Compression level 0..9 (0=store, 1=fastest … 9=smallestSize).</param>
    /// <param name="splitBytes">Optional split threshold; see the sequential overload.</param>
    /// <param name="version">CISO version: <see cref="VersionLz4"/> (default) or <see cref="VersionDeflate"/>.</param>
    /// <param name="jobs">
    /// Worker count for per-sector compression, <c>1..<see cref="MaxParallelism"/></c>.
    /// <c>1</c> keeps the sequential writer.
    /// </param>
    /// <param name="progress">Optional progress channel (reported in sector order).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is outside 0..9, <paramref name="jobs"/> is
    /// outside 1..<see cref="MaxParallelism"/>, or <paramref name="splitBytes"/> is not positive.
    /// </exception>
    public static int CompressToCso(string sourcePath, string? outputCsoPath, int level,
        long? splitBytes, byte version, int jobs,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (level < 0 || level > 9)
            throw new ArgumentOutOfRangeException(nameof(level), "CISO level must be 0..9");

        if (jobs < 1 || jobs > MaxParallelism)
            throw new ArgumentOutOfRangeException(nameof(jobs),
                $"CISO jobs must be 1..{MaxParallelism}");

        if (splitBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(splitBytes));

        if (version != VersionDeflate && version != VersionLz4)
            throw new ArgumentOutOfRangeException(nameof(version), "CISO version must be 1 (DEFLATE) or 2 (LZ4)");

        // A GOD `.data` folder is a container, not a directory to pack.
        XisoImageSource.TryDetect(sourcePath, out XisoImageSourceInfo? sourceInfo);
        bool isGod = sourceInfo is not null && sourceInfo.Format == XisoImageFormat.God;
        if (!isGod && GodHeader.IsGodContainer(sourcePath))
        {
            throw new InvalidDataException(
                $"GOD container '{sourcePath}' is incomplete or has no readable data fragments.");
        }

        bool isDir = Directory.Exists(sourcePath) && !isGod;
        bool isFile = File.Exists(sourcePath);
        if (!isDir && !isFile && !isGod)
            throw new FileNotFoundException($"Source not found: {sourcePath}");

        string output = outputCsoPath ?? DeriveDefaultCsoPath(sourcePath, isDir);
        if (sourceInfo is not null)
        {
            foreach (string part in sourceInfo.Parts)
            {
                if (XisoPaths.AreSamePath(part, output))
                    throw new IOException("Source and destination paths are the same");
            }
        }
        else if (XisoPaths.AreSamePath(sourcePath, output))
        {
            throw new IOException("Source and destination paths are the same");
        }
        if (splitBytes.HasValue && !isDir)
        {
            long sourceLength = sourceInfo?.Length ?? new FileInfo(sourcePath).Length;
            IReadOnlyList<string> sourceParts = sourceInfo?.Parts ?? [sourcePath];
            GuardSplitParts(output, splitBytes.Value, sourceLength, sourceParts);
        }

        string? tempIso = null;
        Stream? containerStream = null;
        string sourceFile = sourcePath;

        try
        {
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
            else if (sourceInfo is not null &&
                     (sourceInfo.Format != XisoImageFormat.Iso || sourceInfo.IsSplit))
            {
                // Split ISO and CISO/CCI/GOD containers already expose one
                // flat, seekable decompressed view; compress it directly
                // instead of materializing a joined temporary ISO (Phase 2).
                if (sourceInfo.Format == XisoImageFormat.Zar)
                {
                    throw new NotSupportedException(
                        "ZArchive (.zar) input is not a sector image; convert it to ISO first.");
                }

                containerStream = XisoImageSource.OpenRead(sourcePath);
            }

            using Stream src = containerStream ?? new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536);
            if (splitBytes.HasValue)
            {
                using CisoSplitOutput split = new(output, splitBytes.Value);
                bool completed = false;
                try
                {
                    CompressStream(src, split, level, version, jobs, progress, ct);
                    completed = true;
                }
                finally
                {
                    if (!completed)
                    {
                        // Close the part handles before deleting: on Windows a
                        // FileShare.None handle blocks File.Delete (Todo #54).
                        // ReSharper disable once DisposeOnUsingVariable
                        split.Dispose();
                        foreach (string part in split.PartPaths)
                            TryDeleteFile(part);
                    }
                }

                foreach (string part in split.PartPaths)
                    Logger.Log($"  part {part} ({new FileInfo(part).Length} bytes)\n");
                Logger.Log($"Compressed {sourcePath} -> {output} ({split.PartPaths.Count} part(s))\n");
            }
            else
            {
                bool created = false;
                try
                {
                    using FileStream dst = new(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    created = true;
                    CompressStream(src, dst, level, version, jobs, progress, ct);
                }
                catch
                {
                    // A failed compress must not leave a truncated .cso that looks
                    // like a finished artifact (Todo #54). Only delete a file this
                    // call actually created: a pre-existing output that merely
                    // failed to open must survive.
                    if (created)
                        TryDeleteFile(output);
                    throw;
                }

                Logger.Log($"Compressed {sourcePath} -> {output} ({new FileInfo(output).Length} bytes)\n");
            }

            return 0;
        }
        finally
        {
            containerStream?.Dispose();
            if (tempIso != null)
            {
                TryDeleteFile(tempIso);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignored: best-effort cleanup
        }
    }

    /// <summary>
    /// Refuses a split compress whose part files would overwrite any source
    /// part: part <c>n</c> holds the global range <c>[(n-1)·split, n·split)</c>,
    /// so a source literally named like one of its own parts (e.g. compressing
    /// <c>game.1.cso</c> to split base <c>game.cso</c>) would be truncated
    /// mid-read. Only parts that can exist for the uncompressed source size are
    /// checked; a split/compressed source contributes every part path.
    /// </summary>
    private static void GuardSplitParts(string output, long splitBytes, long sourceLength,
        IReadOnlyList<string> sourceParts)
    {
        long maxPart = (sourceLength / splitBytes) + 2;
        for (long i = 0L; i < maxPart; i++)
        {
            string part = CisoSplitFile.PartPath(output, i);
            foreach (string sourcePart in sourceParts)
            {
                if (XisoPaths.AreSamePath(sourcePart, part))
                    throw new IOException($"Source and split output part paths are the same: {part}");
            }
        }
    }

    /// <summary>Asynchronous variant of <see cref="CompressToCso(string, string?, int, long?, byte, IProgress{ProgressInfo}?, CancellationToken)"/>.</summary>
    public static async Task<int> CompressToCsoAsync(string sourcePath, string? outputCsoPath = null, int level = 6,
        long? splitBytes = null, byte version = VersionLz4,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        await Task.Run(() => CompressToCso(sourcePath, outputCsoPath, level, splitBytes, version, progress, ct),
                ct)
            .ConfigureAwait(false);

    /// <summary>Asynchronous variant of the bounded-parallel <see cref="CompressToCso(string, string?, int, long?, byte, int, IProgress{ProgressInfo}?, CancellationToken)"/>.</summary>
    public static async Task<int> CompressToCsoAsync(string sourcePath, string? outputCsoPath, int level,
        long? splitBytes, byte version, int jobs,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        await Task.Run(() => CompressToCso(sourcePath, outputCsoPath, level, splitBytes, version, jobs, progress, ct),
                ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Compresses a seekable source stream (uncompressed ISO) to a seekable destination stream (CISO).
    /// Source is read sector-wise (2048 bytes) and written as CISO header + index + per-sector payloads.
    /// Version 2 writes LZ4 payloads (LZ4 frame per sector with the 7-byte header and 4-byte end mark
    /// stripped, as <c>ciso 0.2</c> does); version 1 writes DEFLATE payloads. Split output applies when
    /// the destination is a <see cref="CisoSplitOutput"/>.
    /// </summary>
    public static void CompressStream(Stream source, Stream dest, int level = 6, byte version = VersionLz4,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
        CompressStream(source, dest, level, version, 1, progress, ct);

    /// <summary>
    /// Bounded-parallel <see cref="CompressStream(Stream, Stream, int, byte, IProgress{ProgressInfo}?, CancellationToken)"/>:
    /// up to <paramref name="jobs"/> workers compress sectors in batches and
    /// the payloads are written in sector order, so the resulting CISO is
    /// byte-identical to the single-threaded writer. Progress events keep
    /// sector order.
    /// </summary>
    /// <param name="source">Seekable source stream (plain ISO view).</param>
    /// <param name="dest">Seekable destination stream (CISO, including <see cref="CisoSplitOutput"/>).</param>
    /// <param name="level">Compression level 0..9 (0 = store).</param>
    /// <param name="version"><see cref="VersionLz4"/> or <see cref="VersionDeflate"/>.</param>
    /// <param name="jobs">Worker count 1..<see cref="MaxParallelism"/>; <c>1</c> is sequential.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is outside 0..9, <paramref name="jobs"/> is
    /// outside 1..<see cref="MaxParallelism"/>, or the version is unknown.
    /// </exception>
    public static void CompressStream(Stream source, Stream dest, int level, byte version, int jobs,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (level < 0 || level > 9)
            throw new ArgumentOutOfRangeException(nameof(level), "CISO level must be 0..9");
        if (version != VersionDeflate && version != VersionLz4)
            throw new ArgumentOutOfRangeException(nameof(version), "CISO version must be 1 (DEFLATE) or 2 (LZ4)");
        if (jobs < 1 || jobs > MaxParallelism)
            throw new ArgumentOutOfRangeException(nameof(jobs),
                $"CISO jobs must be 1..{MaxParallelism}");
        if (!source.CanSeek) throw new ArgumentException("Source must be seekable", nameof(source));
        if (!dest.CanSeek) throw new ArgumentException("Destination must be seekable", nameof(dest));

        long uncompressedSize = source.Length;
        // Also handle non-zero Position: we want whole stream
        // Ensure we start from 0
        source.Seek(0, SeekOrigin.Begin);
        dest.Seek(0, SeekOrigin.Begin);

        int totalBlocks = (int)((uncompressedSize + BlockSize - 1) / BlockSize);
        int indexLen = totalBlocks + 1;

        // ciso 0.2 (v2) fixes alignment at 2; classic v1 keeps dynamic alignment:
        // align=0 for <2GB to avoid padding overhead; align=1 for <4GB; align=2 for >=4GB.
        byte align;
        if (version == VersionLz4) align = 2;
        else if (uncompressedSize < 0x80000000L) align = 0;
        else if (uncompressedSize < 0x100000000L) align = 1;
        else align = 2;

        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), (ulong)uncompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), BlockSize);
        header[20] = version;
        header[21] = align;
        header[22] = 0;
        header[23] = 0;

        dest.Write(header, 0, header.Length);

        // Reserve index table (filled later)
        long indexStart = dest.Position;
        byte[] indexBytes = new byte[indexLen * 4];
        dest.Write(indexBytes, 0, indexBytes.Length);

        long dataStart = HeaderSize + (indexLen * 4L);
        long position = dataStart;

        uint[] indexEntries = new uint[indexLen];

        // For progress
        progress?.Report(new ProgressInfo(ProgressInfoType.FileCount, Count: totalBlocks));

        // --ciso-level maps inversely to LZ4 acceleration (level 9 = acceleration 1 = lz4_flex parity).
        int acceleration = Math.Max(1, 10 - level);
        CompressionLevel compLevel = MapLevel(level);

        if (jobs > 1 && level > 0 && totalBlocks > 0)
        {
            CompressParallelSectors(source, dest, level, version, totalBlocks, align, indexEntries,
                ref position, progress, ct, jobs);
        }
        else
        {
            byte[] blockBuf = new byte[BlockSize];

            // Scratch for the v2 payload: [u32 LE block info][LZ4 block data] — the LZ4 frame with
            // its 7-byte header and 4-byte end mark stripped (ciso write.rs).
            byte[]? lz4Scratch = null;
            if (version == VersionLz4 && level > 0)
                lz4Scratch = new byte[4 + Lz4.MaxCompressedOutputSize(BlockSize)];

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
                        byte[] padBuf = new byte[pad];
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

                ReadOnlySpan<byte> dataToWrite;
                uint flagBit;

                if (version == VersionLz4)
                {
                    // v2: index high bit set = compressed, clear = plain.
                    if (level == 0)
                    {
                        dataToWrite = blockBuf;
                        flagBit = 0;
                    }
                    else
                    {
                        int blockLen = Lz4.Compress(blockBuf, lz4Scratch.AsSpan(4), acceleration);
                        // When the block does not compress, the frame stores it raw
                        // (block info carries the 0x80000000 uncompressed bit, as in lz4_flex).
                        uint sizeField = blockLen < BlockSize ? (uint)blockLen : 0x80000000u | BlockSize;
                        BinaryPrimitives.WriteUInt32LittleEndian(lz4Scratch, sizeField);
                        int payloadLen = 4 + Math.Min(blockLen, BlockSize);

                        if (payloadLen + CompressionSavingThreshold >= BlockSize)
                        {
                            dataToWrite = blockBuf;
                            flagBit = 0;
                        }
                        else
                        {
                            dataToWrite = lz4Scratch.AsSpan(0, payloadLen);
                            flagBit = 0x80000000u;
                        }
                    }
                }
                else
                {
                    // v1 classic DEFLATE: index high bit set = plain.
                    byte[] compressed;
                    if (level == 0)
                    {
                        compressed = Array.Empty<byte>();
                    }
                    else
                    {
                        compressed = DeflateCompress(blockBuf, compLevel);
                    }

                    bool usePlain = level == 0 || compressed.Length == 0 ||
                                    compressed.Length + CompressionSavingThreshold >= BlockSize;
                    if (usePlain)
                    {
                        dataToWrite = blockBuf;
                        flagBit = 0x80000000u;
                    }
                    else
                    {
                        dataToWrite = compressed;
                        flagBit = 0;
                    }
                }

                long posShifted = position >> align;
                if (posShifted > 0x7FFFFFFFL)
                    throw new IOException("CISO index overflow: image too large for the CISO format");

                uint entry = (uint)posShifted | flagBit;
                indexEntries[sector] = entry;

                dest.Seek(position, SeekOrigin.Begin);
                dest.Write(dataToWrite);
                position += dataToWrite.Length;

                // Progress: report file added? Use Sector
                progress?.Report(new ProgressInfo(ProgressInfoType.FileAdded, Path: $"/sector/{sector}",
                    Sector: sector, Size: dataToWrite.Length));
            }
        }

        // Final index entry (end of file) — never flagged
        {
            long posShifted = position >> align;
            if (posShifted > 0x7FFFFFFFL)
                throw new IOException("CISO index overflow: image too large for the CISO format");

            indexEntries[indexLen - 1] = (uint)posShifted;
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

    /// <summary>
    /// Bounded-parallel sector compression used by
    /// <see cref="CompressStream(Stream, Stream, int, byte, int, IProgress{ProgressInfo}?, CancellationToken)"/>.
    /// Sectors are read and compressed in batches; the payloads are then
    /// written in sector order with the same alignment and index encoding as
    /// the sequential writer, so the output is byte-identical.
    /// </summary>
    private static void CompressParallelSectors(
        Stream source,
        Stream dest,
        int level,
        byte version,
        int totalBlocks,
        byte align,
        uint[] indexEntries,
        ref long position,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        int jobs)
    {
        int workers = Math.Min(jobs, MaxParallelism);
        // Enough sectors per batch to amortise Parallel.For, capped so a large
        // --jobs never buffers more than a few MiB.
        int batchSectors = Math.Clamp(workers * 16, 64, 1024);
        batchSectors = Math.Min(batchSectors, totalBlocks);

        int lz4Stride = version == VersionLz4 ? 4 + Lz4.MaxCompressedOutputSize(BlockSize) : 0;
        byte[] input = ArrayPool<byte>.Shared.Rent(batchSectors * BlockSize);
        byte[]? lz4Scratch = lz4Stride > 0 ? ArrayPool<byte>.Shared.Rent(batchSectors * lz4Stride) : null;
        // Non-null alias for the worker loop: the LZ4 branch is unreachable when
        // lz4Stride is 0, so the empty fallback is never dereferenced.
        byte[] lz4Buffer = lz4Scratch ?? [];
        byte[]?[] deflatePayloads = new byte[]?[batchSectors];
        int[] lz4Lengths = new int[batchSectors];
        int acceleration = Math.Max(1, 10 - level);
        CompressionLevel compLevel = MapLevel(level);
        byte[] zeroPad = new byte[1 << (align == 0 ? 0 : align)];

        try
        {
            for (int start = 0; start < totalBlocks; start += batchSectors)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(batchSectors, totalBlocks - start);
                int batchBytes = count * BlockSize;

                int read = 0;
                while (read < batchBytes)
                {
                    int n = source.Read(input, read, batchBytes - read);
                    if (n == 0) break;
                    read += n;
                }

                if (read < batchBytes)
                {
                    Array.Clear(input, read, batchBytes - read);
                }

                Parallel.For(0, count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                    i =>
                    {
                        int offset = i * BlockSize;
                        if (version == VersionLz4)
                        {
                            int scratchOffset = i * lz4Stride;
                            int blockLen = Lz4.Compress(input.AsSpan(offset, BlockSize),
                                lz4Buffer.AsSpan(scratchOffset + 4), acceleration);
                            // When the block does not compress, the frame stores it raw
                            // (block info carries the 0x80000000 uncompressed bit, as in lz4_flex).
                            uint sizeField = blockLen < BlockSize ? (uint)blockLen : 0x80000000u | BlockSize;
                            BinaryPrimitives.WriteUInt32LittleEndian(lz4Buffer.AsSpan(scratchOffset), sizeField);
                            int payloadLen = 4 + Math.Min(blockLen, BlockSize);
                            lz4Lengths[i] = payloadLen + CompressionSavingThreshold >= BlockSize ? 0 : payloadLen;
                        }
                        else
                        {
                            byte[] compressed =
                                DeflateCompress(input.AsSpan(offset, BlockSize).ToArray(), compLevel);
                            deflatePayloads[i] =
                                compressed.Length == 0 || compressed.Length + CompressionSavingThreshold >= BlockSize
                                    ? null
                                    : compressed;
                        }
                    });

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int sector = start + i;

                    // Align position before this block
                    if (align != 0)
                    {
                        long alignBytes = 1L << align;
                        long mis = position & (alignBytes - 1);
                        if (mis != 0)
                        {
                            long pad = alignBytes - mis;
                            dest.Seek(position, SeekOrigin.Begin);
                            dest.Write(zeroPad, 0, (int)pad);
                            position += pad;
                        }
                    }

                    ReadOnlySpan<byte> dataToWrite;
                    uint flagBit;
                    if (version == VersionLz4)
                    {
                        if (lz4Lengths[i] == 0)
                        {
                            dataToWrite = input.AsSpan(i * BlockSize, BlockSize);
                            flagBit = 0;
                        }
                        else
                        {
                            dataToWrite = lz4Buffer.AsSpan(i * lz4Stride, lz4Lengths[i]);
                            flagBit = 0x80000000u;
                        }
                    }
                    else
                    {
                        if (deflatePayloads[i] is null)
                        {
                            dataToWrite = input.AsSpan(i * BlockSize, BlockSize);
                            flagBit = 0x80000000u;
                        }
                        else
                        {
                            dataToWrite = deflatePayloads[i];
                            flagBit = 0;
                        }
                    }

                    long posShifted = position >> align;
                    if (posShifted > 0x7FFFFFFFL)
                        throw new IOException("CISO index overflow: image too large for the CISO format");

                    indexEntries[sector] = (uint)posShifted | flagBit;

                    dest.Seek(position, SeekOrigin.Begin);
                    dest.Write(dataToWrite);
                    position += dataToWrite.Length;

                    progress?.Report(new ProgressInfo(ProgressInfoType.FileAdded,
                        Path: $"/sector/{sector}", Sector: sector, Size: dataToWrite.Length));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            if (lz4Scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(lz4Scratch);
            }
        }
    }

    private static byte[] DeflateCompress(byte[] data, CompressionLevel level)
    {
        using MemoryStream ms = new();
        // Use optimal leaveOpen false; but we need to dispose DeflateStream before ToArray
        using (DeflateStream ds = new(ms, level, leaveOpen: true))
        {
            ds.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    private static CompressionLevel MapLevel(int level) =>
        level switch
        {
            0 => CompressionLevel.NoCompression,
            1 or 2 => CompressionLevel.Fastest,
            3 or 4 or 5 or 6 or 7 => CompressionLevel.Optimal,
            8 or 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

    /// <summary>
    /// Derives the default <c>.cso</c> output path for <paramref name="sourcePath"/>
    /// (same rule <see cref="CompressToCso(string, string?, int, long?, byte, IProgress{ProgressInfo}?, CancellationToken)"/> uses when no explicit output is given):
    /// a directory maps to <c>&lt;parent&gt;/&lt;name&gt;.cso</c>, a file maps to its
    /// sibling with the image suffix (including a <c>.1</c> split marker) replaced by <c>.cso</c>.
    /// </summary>
    public static string DeriveDefaultCsoPath(string sourcePath, bool isDir)
    {
        if (isDir)
        {
            // Resolve first: `.`/`..` (and paths ending in a separator) have no
            // usable name/parent as written and used to yield `..cso` in the
            // CWD (Todo #61).
            string trimmed = XisoPaths.TrimTrailingSeparators(Path.GetFullPath(sourcePath));
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
            // Replace any recognized image suffix (including a `.1` split
            // marker and CISO/CCI containers) with `.cso`.
            if (XisoImageSource.HasImageSuffix(file))
            {
                file = XisoImageSource.StripImageSuffix(file) + ".cso";
            }
            else
            {
                string ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext))
                    file = Path.ChangeExtension(file, ".cso");
                else
                    file += ".cso";
            }

            return Path.Combine(dir, file);
        }
    }

    // Internal helper to avoid recursion with public CompressToCso calling PackFromDirectory which might call Compress again
    /// <summary>
    /// Internal packing helper for the CISO pipeline; packs a directory to an exact ISO path
    /// without re-entering <see cref="CompressToCso(string, string?, int, long?, byte, IProgress{ProgressInfo}?, CancellationToken)"/>.
    /// </summary>
    private static class XisoWriterInternal
    {
        /// <summary>
        /// Packs <paramref name="sourceDirectory"/> into <paramref name="outputIsoPath"/>,
        /// creating the output directory first.
        /// </summary>
        /// <param name="sourceDirectory">Source directory whose contents are packed.</param>
        /// <param name="outputIsoPath">Exact destination ISO path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>0 on success, 1 on error.</returns>
        internal static int PackFromDirectoryForCiso(string sourceDirectory, string outputIsoPath, CancellationToken ct)
        {
            // Use XisoWriter.CreateXiso directly with explicit output name to avoid extra .iso
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputIsoPath)) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            // CreateXiso expects rootDirectory + outputDirectory + inName; we want exact path
            // Use PackFromDirectory with outputIsoPath
            return XisoWriter.PackFromDirectory(sourceDirectory, outputIsoPath, cancellationToken: ct);
        }
    }
}
