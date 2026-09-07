using ZARSharp;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <c>rebuild</c> with a <c>.zar</c> sidecar standing in for the
/// <c>&lt;xiso&gt;</c> component (<see cref="XisoRedump.RebuildRedump"/>).
/// Full-size video partitions cannot be fabricated here, so these tests drive the
/// ZAR materialization step and assert the pipeline then fails on the (tiny) video —
/// proving the archive was accepted — plus error paths and scratch cleanup.
/// </summary>
[Collection("Sequential")]
public class XisoZarRebuildTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
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

    private static void PopulateSimple(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "c.txt"), "nested");
    }

    /// <summary>
    /// Scopes <c>Path.GetTempPath()</c> to a per-test parent for the lifetime of
    /// the scope (BUG-TEST-009). The ZAR pipeline hardcodes
    /// <c>Path.GetTempPath()/XISOSharp_zar_*</c> for scratch, so a global
    /// <c>%TEMP%</c> scan flakes under parallel runs/second harnesses. Pointing
    /// <c>TMP</c>/<c>TEMP</c>/<c>TMPDIR</c> at a fresh parent isolates this test's
    /// scratch dirs; assertions then scan only that parent.
    /// Tests run in the Sequential collection, so process-wide env mutation is safe.
    /// </summary>
    private sealed class ScopedTempParent : IDisposable
    {
        public string Parent { get; }

        private readonly string? _savedTmp;
        private readonly string? _savedTemp;
        private readonly string? _savedTmpDir;

        public ScopedTempParent(List<string> track, string prefix)
        {
            var realTemp = Path.GetTempPath();
            Parent = Path.Combine(realTemp, $"{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Parent);
            track.Add(Parent);
            _savedTmp = Environment.GetEnvironmentVariable("TMP");
            _savedTemp = Environment.GetEnvironmentVariable("TEMP");
            _savedTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
            Environment.SetEnvironmentVariable("TMP", Parent);
            Environment.SetEnvironmentVariable("TEMP", Parent);
            Environment.SetEnvironmentVariable("TMPDIR", Parent);
        }

        public string[] ZarScratchDirs()
        {
            return Directory.Exists(Parent)
                ? Directory.GetDirectories(Parent, "XISOSharp_zar_*")
                : [];
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TMP", _savedTmp);
            Environment.SetEnvironmentVariable("TEMP", _savedTemp);
            Environment.SetEnvironmentVariable("TMPDIR", _savedTmpDir);
        }
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _origOut = Logger.Out;
        private readonly TextWriter _origErr = Logger.Error;
        private readonly bool _origQuiet = Logger.Quiet;
        private readonly bool _origRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        public LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _err;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        public string Output => _out.ToString() + _err;

        public void Dispose()
        {
            Logger.Out = _origOut;
            Logger.Error = _origErr;
            Logger.Quiet = _origQuiet;
            Logger.RealQuiet = _origRealQuiet;
            _out.Dispose();
            _err.Dispose();
        }
    }

    [Fact]
    public void RebuildRedump_ZarFileTree_RepacksThenFailsOnVideo()
    {
        var work = CreateTempDir("xiso_zarrb");
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        PopulateSimple(src);
        var zar = Path.Combine(work, "game.zar");
        ZArchiveTool.Pack(src, zar);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        var outRedump = Path.Combine(work, "game.redump.iso");
        using var tempScope = new ScopedTempParent(_tempDirs, "xiso_zarrb_tmp");
        var before = tempScope.ZarScratchDirs();
        Assert.Empty(before);

        string log;
        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.RebuildRedump(zar, fakeVideo, null, null, outRedump, null, quiet: false);
            log = capture.Output;
        }

        Assert.False(ok);
        Assert.Contains("Repacking 3 files", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid XISO", log, StringComparison.Ordinal);
        Assert.Empty(tempScope.ZarScratchDirs());
    }

    [Fact]
    public void RebuildRedump_ZarSingleEmbeddedXiso_UsedVerbatim()
    {
        var work = CreateTempDir("xiso_zarrb");
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        PopulateSimple(src);
        var isoDir = CreateTempDir("xiso_zarrb_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out var isoPath, null, null));
        Assert.NotNull(isoPath);
        var single = Path.Combine(work, "single");
        Directory.CreateDirectory(single);
        File.Copy(isoPath, Path.Combine(single, "game.xiso"));
        var zar = Path.Combine(work, "GAME.ZAR");
        ZArchiveTool.Pack(single, zar);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        var outRedump = Path.Combine(work, "game.redump.iso");
        using var tempScope = new ScopedTempParent(_tempDirs, "xiso_zarrb_tmp");
        Assert.Empty(tempScope.ZarScratchDirs());

        string log;
        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.RebuildRedump(zar, fakeVideo, null, null, outRedump, null, quiet: false);
            log = capture.Output;
        }

        Assert.False(ok);
        Assert.Contains("Using XISO image 'game.xiso'", log, StringComparison.Ordinal);
        Assert.Empty(tempScope.ZarScratchDirs());
    }

    [Fact]
    public void RebuildRedump_CorruptZar_ReturnsFalse()
    {
        var work = CreateTempDir("xiso_zarrb");
        var zar = Path.Combine(work, "bad.zar");
        File.WriteAllBytes(zar, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01]);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        var outRedump = Path.Combine(work, "game.redump.iso");

        string log;
        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.RebuildRedump(zar, fakeVideo, null, null, outRedump, null, quiet: false);
            log = capture.Output;
        }

        Assert.False(ok);
        Assert.Contains("Not a valid ZArchive", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildRedump_EmptyZar_ReturnsFalse()
    {
        // Packing an empty dir yields zero offset records, which the reader rejects —
        // faithful to the C++ reference (zarchivereader.cpp rejects offsetRecords.empty()).
        var work = CreateTempDir("xiso_zarrb");
        var empty = Path.Combine(work, "empty");
        Directory.CreateDirectory(empty);
        var zar = Path.Combine(work, "empty.zar");
        ZArchiveTool.Pack(empty, zar);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        var outRedump = Path.Combine(work, "game.redump.iso");

        string log;
        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.RebuildRedump(zar, fakeVideo, null, null, outRedump, null, quiet: false);
            log = capture.Output;
        }

        Assert.False(ok);
        Assert.Contains("Not a valid ZArchive", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildRedump_MissingZar_ThrowsFileNotFoundException()
    {
        var work = CreateTempDir("xiso_zarrb");
        var missing = Path.Combine(work, "nope.zar");
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);

        Assert.Throws<FileNotFoundException>(() =>
            XisoRedump.RebuildRedump(missing, fakeVideo, null, null,
                Path.Combine(work, "game.redump.iso"), null, quiet: true));
    }

    [Fact]
    public void TryRebuildFromArgs_ZarXiso_InfersVideoAndMaterializes()
    {
        var work = CreateTempDir("xiso_zarrb");
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        PopulateSimple(src);
        var zar = Path.Combine(work, "game.zar");
        ZArchiveTool.Pack(src, zar);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        var outRedump = Path.Combine(work, "game.redump.iso");
        using var tempScope = new ScopedTempParent(_tempDirs, "xiso_zarrb_tmp");
        Assert.Empty(tempScope.ZarScratchDirs());

        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.TryRebuildFromArgs([fakeVideo], zar, outRedump, quiet: false);
            Assert.Contains("Repacking 3 files", capture.Output, StringComparison.Ordinal);
        }

        Assert.False(ok);
        Assert.Empty(tempScope.ZarScratchDirs());
    }

    [Fact]
    public void RebuildRedump_ZarSidecar_Cancellation_ThrowsOperationCanceledException()
    {
        var work = CreateTempDir("xiso_zarrb");
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        PopulateSimple(src);
        var zar = Path.Combine(work, "game.zar");
        ZArchiveTool.Pack(src, zar);
        var fakeVideo = Path.Combine(work, "game.video.iso");
        File.WriteAllBytes(fakeVideo, new byte[2048]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var tempScope = new ScopedTempParent(_tempDirs, "xiso_zarrb_tmp");

        Assert.Throws<OperationCanceledException>(() =>
            XisoRedump.RebuildRedump(zar, fakeVideo, null, null,
                Path.Combine(work, "game.redump.iso"), null, quiet: true, cancellationToken: cts.Token));
        Assert.Empty(tempScope.ZarScratchDirs());
    }
}
