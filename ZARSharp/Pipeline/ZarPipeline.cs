namespace ZARSharp.Pipeline;

/// <summary>
/// Archive pipeline: directory pack, archive extract, and parallel batches
/// with progress, pause, cancellation and collision handling. This is the
/// shared backend behind <see cref="ZArchiveTool"/>, the XISO <c>.zar</c>
/// bridges and the CLI <c>--zar</c> path — one engine, one set of semantics.
/// </summary>
public static class ZarPipeline
{
    /// <summary>
    /// Packs <paramref name="sourceDirectory"/> into a <c>.zar</c> file.
    /// Returns the archive path written (after collision resolution), or null
    /// when <see cref="ZarCollisionPolicy.Skip"/> skips an existing output.
    /// </summary>
    /// <param name="sourceDirectory">Directory to pack (recursively).</param>
    /// <param name="zarPath">
    /// Destination path, or null for <c>&lt;stem&gt;.zar</c> next to the input
    /// (the <c>zarchive.exe</c> default).
    /// </param>
    /// <param name="options">Pack options (level, policy, determinism, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static string? Pack(
        string sourceDirectory,
        string? zarPath = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ZarPipelineOptions();
        zarPath ??= DefaultZarPath(sourceDirectory);
        var resolved = ZarPackEngine.ResolveOutputPath(zarPath, options.CollisionPolicy);
        if (resolved == null)
        {
            return null;
        }

        var source = new DirectoryPackSource(sourceDirectory, options.DeterministicOrder);
        var entries = source.Collect(cancellationToken);
        ZarPackEngine.PackEntries(entries, sourceDirectory, resolved, options, progress, cancellationToken);
        return resolved;
    }

    /// <summary>Packs an arbitrary <see cref="IZarPackSource"/> (directory tree, XISO walk, ...).</summary>
    public static void PackSource(
        IZarPackSource source,
        string zarPath,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zarPath);
        options ??= new ZarPipelineOptions();
        var entries = source.Collect(cancellationToken);
        ZarPackEngine.PackEntries(entries, source.DisplayPath, zarPath, options, progress, cancellationToken);
    }

    /// <summary>
    /// Extracts <paramref name="zarPath"/> into <paramref name="destDir"/>
    /// (created; files overwritten like <c>zarchive.exe</c>). Returns the
    /// extracted file paths relative to the archive root (<c>/</c> separated).
    /// </summary>
    /// <param name="zarPath">Archive file.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">Optional per-entry stdout sink (native <c>main.cpp</c> entry lines).</param>
    public static IReadOnlyList<string> Extract(
        string zarPath,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null) =>
        ZarPackEngine.ExtractEntries(zarPath, destDir, zarPath, options, progress, cancellationToken, log);

    /// <summary>
    /// Packs several directories in parallel (worker count
    /// <c>min(MaxDegreeOfParallelism, items)</c>, like <c>core.py</c>).
    /// One item's failure does not stop the others; per-item outcomes come
    /// back as <see cref="ZarItemResult"/> and batch progress re-bases each
    /// item's ratio into its <c>1/n</c> share.
    /// </summary>
    /// <param name="sourceDirectories">Directories to pack.</param>
    /// <param name="destDir">
    /// Directory receiving the <c>.zar</c> files, or null to write each next
    /// to its source (the <c>zarchive.exe</c> default).
    /// </param>
    /// <param name="options">Pack options (level, policy, workers, ...).</param>
    /// <param name="progress">Batch progress sink (per-item ratios re-based to 1/n shares).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static IReadOnlyList<ZarItemResult> PackBatch(
        IEnumerable<string> sourceDirectories,
        string? destDir = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ZarPipelineOptions();
        var items = sourceDirectories.ToList();
        if (destDir != null)
        {
            Directory.CreateDirectory(destDir);
        }

        if (items.Count == 0)
        {
            return [];
        }

        var snapshot = options;
        var completed = 0;
        var progressLock = new object();
        var results = new ZarItemResult?[items.Count];
        try
        {
            System.Threading.Tasks.Parallel.For(0, items.Count,
                new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = snapshot.ClampedWorkers(items.Count),
                    CancellationToken = cancellationToken,
                },
                i => results[i] = PackOne(items[i], snapshot, destDir, items.Count,
                    progress, progressLock, () => Volatile.Read(ref completed),
                    afterItem: () => Interlocked.Increment(ref completed),
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Items that never started stay null; marked Cancelled below.
        }

        return results.Select((r, i) => r ??
            new ZarItemResult(items[i], null, ZarItemStatus.Cancelled, "Cancelled before start.")).ToList();
    }

    /// <summary>
    /// Extracts several archives in parallel. See <see cref="PackBatch"/> for
    /// the worker/progress/result model. Each archive extracts into
    /// <c>destDir/&lt;stem&gt;_extracted</c> (the <c>zarchive.exe</c> default),
    /// or into <paramref name="destDir"/> itself for a single archive.
    /// </summary>
    public static IReadOnlyList<ZarItemResult> ExtractBatch(
        IEnumerable<string> zarPaths,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ZarPipelineOptions();
        var items = zarPaths.ToList();
        if (items.Count == 0)
        {
            return [];
        }

        var snapshot = options;
        var completed = 0;
        var progressLock = new object();
        var results = new ZarItemResult?[items.Count];
        try
        {
            System.Threading.Tasks.Parallel.For(0, items.Count,
                new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = snapshot.ClampedWorkers(items.Count),
                    CancellationToken = cancellationToken,
                },
                i => results[i] = ExtractOne(items[i], items.Count == 1 ? destDir
                    : Path.Combine(destDir, DefaultExtractName(items[i])),
                    snapshot, items.Count, progress, progressLock,
                    () => Volatile.Read(ref completed),
                    afterItem: () => Interlocked.Increment(ref completed),
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Items that never started stay null; marked Cancelled below.
        }

        return results.Select((r, i) => r ??
            new ZarItemResult(items[i], null, ZarItemStatus.Cancelled, "Cancelled before start.")).ToList();
    }

    /// <summary>
    /// Rolls item outcomes up to one <see cref="ZarProcessState"/>, mirroring
    /// <c>core.py</c>'s end-of-run verdict (cancelled &gt; failed &gt; partial
    /// &gt; completed).
    /// </summary>
    public static ZarProcessState RollUp(IEnumerable<ZarItemResult> items)
    {
        var list = items.ToList();
        if (list.Count == 0 || list.All(r => r.Status == ZarItemStatus.Completed))
        {
            return ZarProcessState.Completed;
        }

        if (list.Any(r => r.Status == ZarItemStatus.Cancelled))
        {
            return ZarProcessState.Cancelled;
        }

        if (list.Any(r => r.Status == ZarItemStatus.Failed))
        {
            return ZarProcessState.Failed;
        }

        return ZarProcessState.Partial;
    }

    private static ZarItemResult PackOne(
        string source, ZarPipelineOptions options, string? destDir, int total,
        IProgress<ZarProgress>? progress, object progressLock, Func<int> completed,
        Action afterItem, CancellationToken cancellationToken)
    {
        var dest = destDir != null ? Path.Combine(destDir, DefaultZarName(source)) : DefaultZarPath(source);
        try
        {
            var itemProgress = Rebasing(progress, total, progressLock, completed);
            var written = Pack(source, dest, options, itemProgress, cancellationToken);
            afterItem();
            if (written == null)
            {
                return new ZarItemResult(source, dest, ZarItemStatus.Skipped, "Output already exists.");
            }

            if (options.DeleteSourceOnSuccess)
            {
                Directory.Delete(source, recursive: true);
            }

            return new ZarItemResult(source, written, ZarItemStatus.Completed);
        }
        catch (OperationCanceledException ex)
        {
            afterItem();
            return new ZarItemResult(source, dest, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex) when (IsBatchFault(ex))
        {
            // Batch isolation like core.py: log the item, run the rest.
            afterItem();
            return new ZarItemResult(source, dest, ZarItemStatus.Failed, ex.Message);
        }
    }

    private static ZarItemResult ExtractOne(
        string zar, string dest, ZarPipelineOptions options, int total,
        IProgress<ZarProgress>? progress, object progressLock, Func<int> completed,
        Action afterItem, CancellationToken cancellationToken)
    {
        try
        {
            var itemProgress = Rebasing(progress, total, progressLock, completed);
            var files = Extract(zar, dest, options, itemProgress, cancellationToken);
            afterItem();
            return new ZarItemResult(zar, dest, ZarItemStatus.Completed,
                FilesProcessed: files.Count);
        }
        catch (OperationCanceledException ex)
        {
            afterItem();
            return new ZarItemResult(zar, dest, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex) when (IsBatchFault(ex))
        {
            afterItem();
            return new ZarItemResult(zar, dest, ZarItemStatus.Failed, ex.Message);
        }
    }

    /// <summary>Faults an item may carry without aborting the batch (I/O, structure, bad paths).</summary>
    private static bool IsBatchFault(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private static IProgress<ZarProgress>? Rebasing(
        IProgress<ZarProgress>? progress, int total, object gate, Func<int> completed) =>
        progress == null ? null : new RebasingProgress(progress, gate, total, completed);

    internal static string DefaultZarName(string sourceDirectory)
    {
        var full = Path.GetFullPath(sourceDirectory);
        var stem = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.GetFileNameWithoutExtension(stem) + ".zar";
    }

    internal static string DefaultZarPath(string sourceDirectory)
    {
        var full = Path.GetFullPath(sourceDirectory);
        var dir = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        return Path.Combine(dir, DefaultZarName(sourceDirectory));
    }

    internal static string DefaultExtractName(string zarPath) =>
        Path.GetFileNameWithoutExtension(zarPath) + "_extracted";

    private sealed class RebasingProgress(
        IProgress<ZarProgress> inner, object gate, int total, Func<int> completed) : IProgress<ZarProgress>
    {
        public void Report(ZarProgress value)
        {
            // Re-base the item ratio into its 1/total share of the batch.
            var filesTotal = Math.Max(1, value.FilesTotal);
            var rebased = value with
            {
                FilesCompleted = (completed() * filesTotal) + value.FilesCompleted,
                FilesTotal = filesTotal * total,
            };
            lock (gate)
            {
                inner.Report(rebased);
            }
        }
    }
}
