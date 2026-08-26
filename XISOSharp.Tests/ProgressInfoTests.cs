namespace XISOSharp.Tests;

/// <summary>
/// Tests for the structured write-progress channel (<see cref="IProgress{T}"/> of
/// <see cref="ProgressInfo"/>) on create and rewrite operations.
/// </summary>
[Collection("Sequential")]
public class ProgressInfoTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_prog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Synchronous event collector. The built-in <see cref="Progress{T}"/> posts callbacks
    /// asynchronously when no synchronization context is present, which would make the
    /// tests racy — this implementation records events inline.
    /// </summary>
    private sealed class CollectingProgress : IProgress<ProgressInfo>
    {
        public List<ProgressInfo> Events { get; } = [];

        public void Report(ProgressInfo value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }

    private static string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiso_prog_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        File.WriteAllText(Path.Combine(root, "root.txt"), "hello");
        File.WriteAllBytes(Path.Combine(root, "sub", "data.bin"), new byte[5000]);
        File.WriteAllText(Path.Combine(root, "sub", "deep", "nested.txt"), "nested");
        return root;
    }

    [Fact]
    public void CreateXiso_ProgressEvents_ReportCountsFirstAndFinishLast()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);
        Assert.Equal(0, result);

        Assert.True(progress.Events.Count >= 5, $"expected at least 5 events, got {progress.Events.Count}");
        Assert.Equal(ProgressInfoType.FileCount, progress.Events[0].Type);
        Assert.Equal(ProgressInfoType.DirCount, progress.Events[1].Type);
        Assert.Equal(ProgressInfoType.FinishedPacking, progress.Events[^1].Type);

        Assert.Equal(3, progress.Events[0].Count); // root.txt, data.bin, nested.txt
        Assert.Equal(3, progress.Events[1].Count); // sub, sub/deep, empty
    }

    [Fact]
    public void CreateXiso_ProgressEvents_ReportPathsAndSizes()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);

        var dirs = progress.Events.Where(e => e.Type == ProgressInfoType.DirAdded).ToList();
        var files = progress.Events.Where(e => e.Type == ProgressInfoType.FileAdded).ToList();

        var dirPaths = dirs.Select(d => d.Path ?? "").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["/", "/empty", "/sub", "/sub/deep"], dirPaths);

        var filePaths = files.Select(f => f.Path ?? "").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["/root.txt", "/sub/data.bin", "/sub/deep/nested.txt"], filePaths);

        var dataBin = files.Single(f => string.Equals(f.Path, "/sub/data.bin", StringComparison.Ordinal));
        Assert.Equal(5000, dataBin.Size);
        Assert.True(dataBin.Sector > 0);

        var rootTxt = files.Single(f => string.Equals(f.Path, "/root.txt", StringComparison.Ordinal));
        Assert.Equal(5, rootTxt.Size);
    }

    [Fact]
    public void CreateXiso_ProgressEvents_ParentDirectoryPrecedesChildren()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);

        var indexOf = progress.Events
            .Select((e, i) => (e, i))
            .ToDictionary(x => (x.e.Type, x.e.Path), x => x.i);

        Assert.True(indexOf[(ProgressInfoType.DirAdded, "/sub")] < indexOf[(ProgressInfoType.DirAdded, "/sub/deep")]);
        Assert.True(indexOf[(ProgressInfoType.DirAdded, "/sub")] <
                    indexOf[(ProgressInfoType.FileAdded, "/sub/data.bin")]);
        Assert.True(indexOf[(ProgressInfoType.DirAdded, "/sub/deep")] <
                    indexOf[(ProgressInfoType.FileAdded, "/sub/deep/nested.txt")]);
    }

    [Fact]
    public void CreateXiso_ProgressEvents_EmptyDirectoryEmitsDirAddedOnly()
    {
        var src = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        File.WriteAllText(Path.Combine(src, "keep.txt"), "x");
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);

        Assert.Contains(progress.Events,
            e => e.Type == ProgressInfoType.DirAdded && string.Equals(e.Path, "/empty", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Events,
            e => e.Type == ProgressInfoType.FileAdded && string.Equals(e.Path, "/empty", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateXiso_ProgressEvents_FileSizesSumToSourceBytes()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);

        var expected = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var reported = progress.Events.Where(e => e.Type == ProgressInfoType.FileAdded).Sum(e => e.Size);

        Assert.Equal(expected, reported);
    }

    [Fact]
    public void CreateXiso_ProgressEvents_NoFinishedPackingOnFailure()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        // A throwing byte-progress callback aborts the write phase mid-operation.
        // (The first invocation — the pre-write total report — is outside the write
        // try-block, so the callback must fail on a later invocation to exercise it.)
        var calls = 0;
        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out _, null,
            (_, _) =>
            {
                if (++calls > 1) throw new InvalidOperationException("abort");
            },
            progress: progress);

        Assert.Equal(1, result);
        Assert.DoesNotContain(progress.Events, e => e.Type == ProgressInfoType.FinishedPacking);
        Assert.Contains(progress.Events, e => e.Type == ProgressInfoType.FileCount);
    }

    [Fact]
    public void CreateXiso_EmptySource_ReportsZeroCounts()
    {
        var src = CreateTempDir(); // empty
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out _, null, null, progress: progress);
        Assert.Equal(0, result);

        Assert.Equal(ProgressInfoType.FileCount, progress.Events[0].Type);
        Assert.Equal(0, progress.Events[0].Count);
        Assert.Equal(ProgressInfoType.DirCount, progress.Events[1].Type);
        Assert.Equal(0, progress.Events[1].Count);
        Assert.Contains(progress.Events,
            e => e.Type == ProgressInfoType.DirAdded && string.Equals(e.Path, "/", StringComparison.Ordinal));
        Assert.Equal(ProgressInfoType.FinishedPacking, progress.Events[^1].Type);
    }

    [Fact]
    public void Rewrite_WithProgress_ReportsEvents()
    {
        var src = CreateSourceTree();
        var createDir = CreateTempDir();
        XisoWriter.CreateXiso(src, createDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var rewriteDir = CreateTempDir();
        var progress = new CollectingProgress();

        var result = XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath, progress: progress);
        Assert.Equal(0, result);
        Assert.NotNull(rewrittenPath);

        Assert.True(progress.Events.Count >= 5, $"expected at least 5 events, got {progress.Events.Count}");
        Assert.Equal(ProgressInfoType.FileCount, progress.Events[0].Type);
        Assert.Equal(ProgressInfoType.DirCount, progress.Events[1].Type);
        Assert.Equal(ProgressInfoType.FinishedPacking, progress.Events[^1].Type);
        Assert.Contains(progress.Events,
            e => e.Type == ProgressInfoType.FileAdded &&
                 string.Equals(e.Path, "/sub/data.bin", StringComparison.Ordinal));
        Assert.Contains(progress.Events,
            e => e.Type == ProgressInfoType.DirAdded && string.Equals(e.Path, "/sub/deep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateXisoAsync_WithProgress_ReportsEvents()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var progress = new CollectingProgress();

        (int result, _) = await XisoWriter.CreateXisoAsync(
            src, outputDir, null, null, null, null, progress: progress);
        Assert.Equal(0, result);

        Assert.Equal(ProgressInfoType.FileCount, progress.Events[0].Type);
        Assert.Equal(ProgressInfoType.FinishedPacking, progress.Events[^1].Type);
        Assert.Equal(3, progress.Events.Count(e => e.Type == ProgressInfoType.FileAdded));
    }
}