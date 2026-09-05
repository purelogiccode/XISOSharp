namespace ZARSharp.Pipeline;

using System.Diagnostics;

/// <summary>
/// Shared pack/extract engine behind <see cref="ZarPipeline"/>,
/// <see cref="ZArchiveTool"/> and the XISO <c>.zar</c> bridges. The loops
/// mirror <c>src/main.cpp</c> (ZArchive 0.1.2) exactly — same call sequence,
/// same refusal/delete-incomplete semantics, same error strings — while
/// adding byte/file progress, pause and cancellation.
/// </summary>
public static class ZarPackEngine
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Resolves <paramref name="wantedPath"/> under <paramref name="policy"/>:
    /// returns the path to write, or null to skip when it already exists.
    /// </summary>
    /// <exception cref="IOException">When <see cref="ZarCollisionPolicy.Fail"/> refuses.</exception>
    public static string? ResolveOutputPath(string wantedPath, ZarCollisionPolicy policy)
    {
        if (!File.Exists(wantedPath) && !Directory.Exists(wantedPath))
        {
            return wantedPath;
        }

        return policy switch
        {
            ZarCollisionPolicy.Fail =>
                throw new IOException($"The output file already exists: {wantedPath}"),
            ZarCollisionPolicy.Skip => null,
            ZarCollisionPolicy.Overwrite => Overwrite(wantedPath),
            ZarCollisionPolicy.AutoRename => FirstFreeSibling(wantedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

        static string Overwrite(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return path;
        }

        static string FirstFreeSibling(string path)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var stem = Path.GetFileNameWithoutExtension(path);
            var suffix = Path.GetExtension(path);
            for (var n = 1; ; n++)
            {
                var candidate = Path.Combine(dir, $"{stem}_{n}{suffix}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Packs pre-collected <paramref name="entries"/> into
    /// <paramref name="zarPath"/> (must already be collision-resolved).
    /// </summary>
    public static void PackEntries(
        IReadOnlyList<ZarPackEntry> entries,
        string displayPath,
        string zarPath,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ZarPipelineOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var pause = options.Pause;
        long filesTotal = 0;
        long bytesTotal = 0;
        foreach (var e in entries)
        {
            if (!e.IsDirectory)
            {
                filesTotal++;
                bytesTotal += Math.Max(0, e.Length);
            }
        }

        var clock = Stopwatch.StartNew();
        long filesCompleted = 0;
        long bytesCompleted = 0;
        void Report(string current) => progress?.Report(new ZarProgress(
            ZarOperation.Pack, displayPath, zarPath, current,
            filesCompleted, filesTotal, bytesCompleted, bytesTotal));

        try
        {
            using var output = new FileStream(zarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            using var writer = new ZArchiveWriter(output, options.ResolveCompressor());
            var buffer = new byte[ZArchiveCommon.CompressedBlockSize];

            Report(string.Empty);
            foreach (var entry in entries)
            {
                pause.WaitIfPaused(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    if (!writer.MakeDir(entry.RelativePath, recursive: false))
                    {
                        throw new InvalidOperationException($"Failed to create directory {entry.RelativePath}");
                    }

                    continue;
                }

                if (entry.OpenRead == null)
                {
                    throw new ZarEntryCreateException($"Failed to create archive file {entry.RelativePath}");
                }

                if (!writer.StartNewFile(entry.RelativePath))
                {
                    throw new ZarEntryCreateException($"Failed to create archive file {entry.RelativePath}");
                }

                using var input = OpenEntryInput(entry);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    pause.WaitIfPaused(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.AppendData(buffer.AsSpan(0, read));
                    bytesCompleted += read;
                    if (clock.Elapsed >= ProgressInterval)
                    {
                        Report(entry.RelativePath);
                        clock.Restart();
                    }
                }

                filesCompleted++;
                Report(entry.RelativePath);
            }

            writer.Finalize();
            Report(string.Empty);
        }
        catch
        {
            try
            {
                File.Delete(zarPath);
            }
            catch
            {
                /* best effort */
            }

            throw;
        }
    }

    /// <summary>Opens an entry for reading, mapping I/O faults to the native <c>-15</c> fault.</summary>
    /// <exception cref="ZarInputOpenException">When the input file cannot be opened.</exception>
    private static Stream OpenEntryInput(ZarPackEntry entry)
    {
        try
        {
            return entry.OpenRead!();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ZarInputOpenException($"Failed to open input file {entry.RelativePath}", ex);
        }
    }

    private sealed record ExtractPlanEntry(string SrcPath, string RelativePath, bool IsDirectory, ulong Size, string LogLine);

    /// <summary>
    /// Extracts <paramref name="zarPath"/> into <paramref name="destDir"/>
    /// (created; files overwritten like <c>zarchive.exe</c>). Returns the
    /// extracted file paths relative to the archive root (<c>/</c> separated).
    /// </summary>
    /// <param name="zarPath">Archive file.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="displayPath">Label used in progress reports.</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">
    /// Optional per-entry stdout sink. When set, every visited entry is logged
    /// as <c>main.cpp</c> prints it (<c>srcPath/name</c>, i.e. a leading
    /// <c>/</c> for top-level entries), directories included, in preorder.
    /// </param>
    /// <exception cref="FileNotFoundException">When the archive is missing.</exception>
    /// <exception cref="ZarArchiveOpenException">When the archive cannot be opened (corrupt header).</exception>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static IReadOnlyList<string> ExtractEntries(
        string zarPath,
        string destDir,
        string? displayPath = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        if (!File.Exists(zarPath))
        {
            throw new FileNotFoundException($"Unable to find archive file: {zarPath}");
        }

        using var reader = ZArchiveReader.TryOpen(zarPath) ??
            throw new ZarArchiveOpenException("Failed to open ZArchive.");

        return ExtractOpen(reader, zarPath, destDir, options, progress, cancellationToken, log);
    }

    /// <summary>
    /// Extracts an already-open <paramref name="reader"/> into
    /// <paramref name="destDir"/>. See <see cref="ExtractEntries"/>.
    /// </summary>
    /// <param name="reader">Open archive reader.</param>
    /// <param name="displayPath">Label used in progress reports.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">Optional per-entry stdout sink (see <see cref="ExtractEntries"/>).</param>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static IReadOnlyList<string> ExtractOpen(
        ZArchiveReader reader,
        string displayPath,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new ZarPipelineOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var pause = options.Pause;

        var plan = new List<ExtractPlanEntry>();
        CollectEntries(reader, string.Empty, string.Empty, plan, cancellationToken);

        long filesTotal = 0;
        long bytesTotal = 0;
        foreach (var p in plan)
        {
            if (!p.IsDirectory)
            {
                filesTotal++;
                bytesTotal += (long)p.Size;
            }
        }

        Directory.CreateDirectory(destDir);
        var files = new List<string>();
        var clock = Stopwatch.StartNew();
        long filesCompleted = 0;
        long bytesCompleted = 0;
        void Report(string current) => progress?.Report(new ZarProgress(
            ZarOperation.Extract, displayPath, destDir, current,
            filesCompleted, filesTotal, bytesCompleted, bytesTotal));

        Report(string.Empty);
        var buffer = new byte[ZArchiveCommon.CompressedBlockSize];
        foreach (var item in plan)
        {
            pause.WaitIfPaused(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Native stdout order: each entry line prints before its bytes
            // are extracted, so a mid-archive failure leaves the same prefix.
            log?.Invoke(item.LogLine);
            var outPath = Path.Combine(destDir, item.RelativePath);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(outPath);
                continue;
            }

            var handle = reader.LookUp(item.SrcPath);
            if (handle == ZArchiveReader.InvalidNode || !reader.IsFile(handle))
            {
                throw new InvalidOperationException($"Unable to extract file: {item.SrcPath}");
            }

            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            ulong offset = 0;
            while (true)
            {
                pause.WaitIfPaused(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.ReadFromFile(handle, offset, buffer);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, (int)read);
                offset += read;
                bytesCompleted += (long)read;
                if (clock.Elapsed >= ProgressInterval)
                {
                    Report(item.RelativePath);
                    clock.Restart();
                }
            }

            if (offset != reader.GetFileSize(handle))
            {
                throw new InvalidOperationException($"Extraction failed: {item.SrcPath}");
            }

            files.Add(item.RelativePath);
            filesCompleted++;
            Report(item.RelativePath);
        }

        Report(string.Empty);
        return files;
    }

    private static void CollectEntries(
        ZArchiveReader reader, string srcPath, string relPath,
        List<ExtractPlanEntry> plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dirHandle = reader.LookUp(srcPath);
        if (dirHandle == ZArchiveReader.InvalidNode || !reader.IsDirectory(dirHandle))
        {
            throw new InvalidOperationException($"Directory not found in archive: '{srcPath}'.");
        }

        var count = reader.GetDirEntryCount(dirHandle);
        for (uint i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.GetDirEntry(dirHandle, i, out var entry))
            {
                throw new InvalidOperationException("Directory contains invalid node.");
            }

            var childSrc = string.IsNullOrEmpty(srcPath) ? entry.Name : srcPath + "/" + entry.Name;
            var childRel = string.IsNullOrEmpty(relPath) ? entry.Name : relPath + "/" + entry.Name;
            // Native stdout quirk, kept byte-identical: the root call passes
            // srcPath "" so top-level entries print with a leading "/".
            var logLine = string.IsNullOrEmpty(srcPath) ? "/" + entry.Name : childSrc;
            if (entry.IsDirectory)
            {
                plan.Add(new ExtractPlanEntry(childSrc, childRel, true, 0, logLine));
                CollectEntries(reader, childSrc, childRel, plan, cancellationToken);
            }
            else
            {
                plan.Add(new ExtractPlanEntry(childSrc, childRel, false, entry.Size, logLine));
            }
        }
    }
}
