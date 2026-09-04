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

    private static string[] ZarScratchDirs()
    {
        return Directory.GetDirectories(Path.GetTempPath(), "XISOSharp_zar_*");
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
        var before = ZarScratchDirs();

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
        Assert.Equal(before, ZarScratchDirs());
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
        var before = ZarScratchDirs();

        string log;
        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.RebuildRedump(zar, fakeVideo, null, null, outRedump, null, quiet: false);
            log = capture.Output;
        }

        Assert.False(ok);
        Assert.Contains("Using XISO image 'game.xiso'", log, StringComparison.Ordinal);
        Assert.Equal(before, ZarScratchDirs());
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
        var before = ZarScratchDirs();

        bool ok;
        using (var capture = new LogCapture())
        {
            ok = XisoRedump.TryRebuildFromArgs([fakeVideo], zar, outRedump, quiet: false);
            Assert.Contains("Repacking 3 files", capture.Output, StringComparison.Ordinal);
        }

        Assert.False(ok);
        Assert.Equal(before, ZarScratchDirs());
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

        Assert.Throws<OperationCanceledException>(() =>
            XisoRedump.RebuildRedump(zar, fakeVideo, null, null,
                Path.Combine(work, "game.redump.iso"), null, quiet: true, cancellationToken: cts.Token));
        Assert.Empty(ZarScratchDirs());
    }
}