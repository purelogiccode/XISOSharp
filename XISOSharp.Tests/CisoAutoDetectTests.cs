namespace XISOSharp.Tests;

/// <summary>
/// Tests for transparent <c>.cso</c> input in the read/rewrite verbs
/// (<see cref="XisoReader.Extract"/>, <c>List</c>, <c>Tree</c>, <c>UnpackImage</c>,
/// <c>Rewrite</c>), mirroring <c>xdvdfs-cli/src/img.rs::open_image</c>.
/// Each test compresses a freshly packed XISO and asserts the verb behaves
/// identically on the <c>.iso</c> and the <c>.cso</c>.
/// </summary>
[Collection("Sequential")]
public class CisoAutoDetectTests : IDisposable
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

    private static string CreateIso(string work)
    {
        var src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        PopulateSimple(src);
        var isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out var isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static string Compress(string iso, string csoPath, long? splitBytes = null, byte? version = null)
    {
        Assert.Equal(0,
            CisoWriter.CompressToCso(iso, csoPath, splitBytes: splitBytes, version: version ?? CisoWriter.VersionLz4));
        return csoPath;
    }

    private static void AssertSameTree(string dirA, string dirB)
    {
        var filesA = Directory.GetFiles(dirA, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirA, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var filesB = Directory.GetFiles(dirB, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirB, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(filesA, filesB);
        foreach (var rel in filesA)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dirA, rel)), File.ReadAllBytes(Path.Combine(dirB, rel)));
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
    public void Extract_CsoInput_MatchesIsoExtraction()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));
        var outIso = Path.Combine(work, "from_iso");
        var outCso = Path.Combine(work, "from_cso");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(cso, outCso, llCompat: false));
        AssertSameTree(outIso, outCso);
    }

    [Fact]
    public void Extract_DeflateCsoInput_MatchesIsoExtraction()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"), version: CisoWriter.VersionDeflate);
        var outIso = Path.Combine(work, "from_iso");
        var outCso = Path.Combine(work, "from_cso");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(cso, outCso, llCompat: false));
        AssertSameTree(outIso, outCso);
    }

    [Fact]
    public void ListAndTree_CsoInput_SucceedAndNameContainer()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));

        string listCsoLog, treeCsoLog, listIsoLog;
        using (var capture = new LogCapture())
        {
            Assert.Equal(0, XisoReader.List(cso, llCompat: false));
            listCsoLog = capture.Output;
        }

        using (var capture = new LogCapture())
        {
            Assert.Equal(0, XisoReader.Tree(cso, llCompat: false));
            treeCsoLog = capture.Output;
        }

        using (var capture = new LogCapture())
        {
            Assert.Equal(0, XisoReader.List(iso, llCompat: false));
            listIsoLog = capture.Output;
        }

        Assert.Contains("listing game.cso", listCsoLog, StringComparison.Ordinal);
        Assert.Contains("a.txt", treeCsoLog, StringComparison.Ordinal);

        // Same entries, only the container name differs.
        Assert.Equal(
            listIsoLog.Replace("game.iso", "IMG", StringComparison.Ordinal),
            listCsoLog.Replace("game.cso", "IMG", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpackImage_CsoInput_MatchesIsoUnpack()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));
        var outIso = Path.Combine(work, "from_iso");
        var outCso = Path.Combine(work, "from_cso");

        Assert.Equal(0, XisoReader.UnpackImage(iso, outIso));
        Assert.Equal(0, XisoReader.UnpackImage(cso, outCso));
        AssertSameTree(outIso, outCso);
    }

    [Fact]
    public void Rewrite_CsoInput_ByteIdenticalToRewriteIso()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));
        var outIsoDir = Path.Combine(work, "rw_iso");
        var outCsoDir = Path.Combine(work, "rw_cso");
        Directory.CreateDirectory(outIsoDir);
        Directory.CreateDirectory(outCsoDir);

        Assert.Equal(0, XisoReader.Rewrite(iso, outIsoDir, out var isoOut));
        Assert.Equal(0, XisoReader.Rewrite(cso, outCsoDir, out var csoOut));

        Assert.NotNull(isoOut);
        Assert.NotNull(csoOut);
        Assert.Equal("game.iso", Path.GetFileName(csoOut));
        Assert.Equal(File.ReadAllBytes(isoOut), File.ReadAllBytes(csoOut));
    }

    [Fact]
    public void Extract_SplitCsoInput_MatchesIsoExtraction()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        Compress(iso, Path.Combine(work, "game.cso"), splitBytes: 1 << 20);
        var firstPart = Path.Combine(work, "game.1.cso");
        Assert.True(File.Exists(firstPart));
        var outIso = Path.Combine(work, "from_iso");
        var outCso = Path.Combine(work, "from_split");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(firstPart, outCso, llCompat: false));
        AssertSameTree(outIso, outCso);
    }

    [Fact]
    public void Extract_CsoInput_DefaultDirUsesGameStem()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(work);
        try
        {
            Assert.Equal(0, XisoReader.Extract(cso, null, llCompat: false));
            Assert.True(Directory.Exists(Path.Combine(work, "game")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(work, "game", "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(work, "game.cso")));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void Extract_RenamedCsoOld_StillDetectedByMagic()
    {
        // The CLI rewrite flow renames game.cso -> game.cso.old before decoding;
        // the CISO magic sniff must still route it to the decompressed view.
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));
        var renamed = Path.Combine(work, "game.cso.old");
        File.Move(cso, renamed);
        var outIso = Path.Combine(work, "from_iso");
        var outCso = Path.Combine(work, "from_renamed");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(renamed, outCso, llCompat: false));
        AssertSameTree(outIso, outCso);
    }

    [Fact]
    public void IsOptimizedImage_CsoMatchesIso()
    {
        var work = CreateTempDir("xiso_csoauto");
        var iso = CreateIso(work);
        var cso = Compress(iso, Path.Combine(work, "game.cso"));

        Assert.Equal(XisoReader.IsOptimizedImage(iso), XisoReader.IsOptimizedImage(cso));
    }
}