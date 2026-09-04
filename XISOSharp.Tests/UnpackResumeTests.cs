using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for TODO #13 (xdvdfs #190): resume an interrupted unpack via
/// <see cref="UnpackOptions.SkipExisting"/> — files already on disk with a
/// matching size are skipped instead of rewritten, in unpack/extract and
/// copy-out paths, while cancellation is still honored promptly.
/// </summary>
[Collection("Sequential")]
public class UnpackResumeTests : IDisposable
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

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static UnpackOptions Skip() => new() { SkipExisting = true };

    private static readonly DateTime PinnedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string CreateSourceTree()
    {
        var root = CreateTempDir("xiso_resume_src");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "data"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "empty.txt"), string.Empty);
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), new string('B', 5000));

        var payload = new byte[20000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(Path.Combine(root, "data", "c.bin"), payload);
        return root;
    }

    private string CreateIso(string srcDir, string isoName)
    {
        var outputDir = CreateTempDir("xiso_resume_iso");
        var isoPath = Path.Combine(outputDir, isoName);
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(isoPath, created);
        return isoPath;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    private static void PinTimes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, PinnedTime);
    }

    private sealed class SyncProgress : IProgress<ProgressInfo>
    {
        private readonly Action<ProgressInfo> _onReport;

        public SyncProgress(Action<ProgressInfo> onReport) => _onReport = onReport;

        public void Report(ProgressInfo value) => _onReport(value);
    }

    [Fact]
    public void SkipExisting_ResumesPartialUnpack_OnlyMissingFilesWritten()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir("xiso_resume_dest");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));
        Assert.Equal(HashTree(src), HashTree(dest));

        // Pin every extracted file, then simulate an interrupted run by deleting two.
        PinTimes(dest);
        File.Delete(Path.Combine(dest, "sub", "b.txt"));
        File.Delete(Path.Combine(dest, "data", "c.bin"));

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest, options: Skip()));

        // Missing files are restored with identical content; the survivors
        // (including the zero-byte file) still carry the pinned timestamp,
        // proving they were skipped rather than rewritten.
        Assert.Equal(HashTree(src), HashTree(dest));
        Assert.Equal(PinnedTime, File.GetLastWriteTimeUtc(Path.Combine(dest, "a.txt")));
        Assert.Equal(PinnedTime, File.GetLastWriteTimeUtc(Path.Combine(dest, "empty.txt")));
    }

    [Fact]
    public void SkipExisting_LeavesSameSizeFileUntouched()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir("xiso_resume_dest");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));

        // Same byte count, different content: size-match means "already done".
        var target = Path.Combine(dest, "a.txt");
        File.WriteAllText(target, "HELLO");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest, options: Skip()));
        Assert.Equal("HELLO", File.ReadAllText(target));
    }

    [Fact]
    public void WithoutFlag_OverwritesExistingFiles()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir("xiso_resume_dest");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));

        var target = Path.Combine(dest, "a.txt");
        File.WriteAllText(target, "HELLO");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));
        Assert.Equal("hello", File.ReadAllText(target));
    }

    [Fact]
    public void CancelledUnpack_ThrowsOperationCanceledException_RestoresWorkingDirectory()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir("xiso_resume_dest");

        using var cts = new CancellationTokenSource();
        var written = 0;
        var progress = new SyncProgress(info =>
        {
            if (info.Type == ProgressInfoType.FileAdded && ++written == 2)
                cts.Cancel();
        });

        var originalCwd = Directory.GetCurrentDirectory();
        var ex = Record.Exception(() => XisoReader.DecodeXiso(isoPath, dest, ExtractMode.Extract,
            out _, llCompat: false, cancellationToken: cts.Token,
            progress: progress, unpackOptions: new UnpackOptions()));
        Assert.IsType<OperationCanceledException>(ex);

        // The interrupted run must not leak its destination chdir ...
        Assert.Equal(originalCwd, Directory.GetCurrentDirectory());

        // ... and must have stopped early with a partial tree ...
        Assert.Equal(2, Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count());

        // ... which a skip-existing re-run completes to identical hashes.
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest, options: Skip()));
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void CopyOut_SkipExisting_SkipsIdenticalFile_ResumesMissing()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var destDir = CreateTempDir("xiso_resume_copyout");
        var dest = Path.Combine(destDir, "b.txt");

        XisoReader.CopyOut(isoPath, "/sub/b.txt", dest);
        Assert.True(File.Exists(dest));

        // Same-size tampering survives a skip-existing copy-out ...
        var payload = new byte[5000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)'X';
        File.WriteAllBytes(dest, payload);

        XisoReader.CopyOut(isoPath, "/sub/b.txt", dest, Skip());
        Assert.Equal(payload, File.ReadAllBytes(dest));

        // ... while a missing destination is restored with identical content.
        File.Delete(dest);
        XisoReader.CopyOut(isoPath, "/sub/b.txt", dest, Skip());
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "sub", "b.txt")), File.ReadAllBytes(dest));
    }

    [Fact]
    public void CopyOut_SkipExisting_ResumesPartialDirectory()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir("xiso_resume_copydir");

        XisoReader.CopyOut(isoPath, "/sub", dest);
        PinTimes(dest);
        File.Delete(Path.Combine(dest, "b.txt"));

        XisoReader.CopyOut(isoPath, "/sub", dest, Skip());

        Assert.Equal(HashTree(Path.Combine(src, "sub")), HashTree(dest));
    }

    [Fact]
    public void SequentialUnpacks_WithNullOutputPath_RestoreWorkingDirectory()
    {
        // Regression lock: an ISO-named (null outputPath) unpack chdirs into the
        // destination; the caller's directory must be restored so a following
        // image (e.g. --batch without -d) resolves against the right directory
        // instead of nesting into the previous ISO's subdirectory.
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var workDir = CreateTempDir("xiso_resume_cwd");

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(workDir);

            Assert.Equal(0, XisoReader.UnpackImage(isoPath));
            Assert.Equal(workDir, Directory.GetCurrentDirectory());

            Assert.Equal(0, XisoReader.UnpackImage(isoPath));
            Assert.Equal(workDir, Directory.GetCurrentDirectory());

            Assert.Equal(HashTree(src), HashTree(Path.Combine(workDir, "game")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void ShouldSkip_MatchesOnlyCompleteFilesWhenEnabled()
    {
        var dir = CreateTempDir("xiso_resume_shouldskip");
        var file = Path.Combine(dir, "f.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var skip = Skip();
        Assert.True(skip.ShouldSkip(file, 4));
        Assert.False(skip.ShouldSkip(file, 3));
        Assert.False(skip.ShouldSkip(Path.Combine(dir, "missing.bin"), 4));
        Assert.False(skip.ShouldSkip("", 4));
        Assert.False(skip.ShouldSkip(file, -1));

        Assert.False(new UnpackOptions().ShouldSkip(file, 4));
    }
}