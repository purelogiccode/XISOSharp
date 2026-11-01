using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// The raw-sector redump modes (video/filler/seed/wipe/trim/petrify/update/ZAR)
/// must refuse compressed and split containers up front. Before the guard, a
/// <c>.cci</c>/<c>.cso</c> input was parsed as a raw XISO and failed with a
/// confusing TOC error (or produced garbage output); a split part and a
/// <c>.zar</c> had the same problem.
/// </summary>
[Collection("Sequential")]
public sealed class RedumpInputGuardTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    public RedumpInputGuardTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedLoggerOut = Logger.Out;
        _savedLoggerError = Logger.Error;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
    }

    public void Dispose()
    {
        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedLoggerOut;
        Logger.Error = _savedLoggerError;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _outCapture.Dispose();
        _errCapture.Dispose();
        foreach (string dir in _tempDirs)
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

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("--wipe")]
    [InlineData("--trim")]
    [InlineData("--random")]
    [InlineData("--seed")]
    [InlineData("--petrify")]
    [InlineData("--update")]
    [InlineData("--zar")]
    public void RedumpMode_CciInput_IsRefusedCleanly(string mode)
    {
        string work = CreateTempDir("xiso_redump_guard");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, splitBytes: 0));

        int rc = Program.Main([mode, "-y", cci]);

        Assert.Equal(1, rc);
        string text = _outCapture.ToString() + _errCapture.ToString();
        Assert.Contains("require a plain ISO", text, StringComparison.Ordinal);
        Assert.Contains("decompress", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fatal", text, StringComparison.Ordinal);
        AssertNoDerivedOutputs(work, "game");
    }

    [Fact]
    public void RedumpMode_CisoInput_IsRefusedCleanly()
    {
        string work = CreateTempDir("xiso_redump_guard");
        string iso = CreateIso(work);
        string cso = Path.Combine(work, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, splitBytes: null));

        int rc = Program.Main(["--wipe", "-y", cso]);

        Assert.Equal(1, rc);
        string text = _outCapture.ToString() + _errCapture.ToString();
        Assert.Contains("CISO", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decompress", text, StringComparison.OrdinalIgnoreCase);
        AssertNoDerivedOutputs(work, "game");
    }

    [Fact]
    public void RedumpMode_SplitIsoPart_IsRefusedCleanly()
    {
        string work = CreateTempDir("xiso_redump_guard");
        string iso = CreateIso(work);
        string splitDir = Path.Combine(work, "split");
        Directory.CreateDirectory(splitDir);
        IReadOnlyList<string> parts = XisoSplitter.Split(iso, Path.Combine(splitDir, "game.iso"), 256 * 1024);
        Assert.True(parts.Count >= 2);

        int rc = Program.Main(["--wipe", "-y", parts[0]]);

        Assert.Equal(1, rc);
        string text = _outCapture.ToString() + _errCapture.ToString();
        Assert.Contains("split-ISO", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("join", text, StringComparison.OrdinalIgnoreCase);
        AssertNoDerivedOutputs(splitDir, "game");
    }

    [Fact]
    public void RedumpMode_ZarInput_IsRefusedCleanly()
    {
        string work = CreateTempDir("xiso_redump_guard");
        string iso = CreateIso(work);
        string zar = Path.Combine(work, "game.zar");
        Assert.True(XisoZarchive.CreateZar(iso, zar));

        int rc = Program.Main(["--wipe", "-y", zar]);

        Assert.Equal(1, rc);
        string text = _outCapture.ToString() + _errCapture.ToString();
        Assert.Contains("ZArchive", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert", text, StringComparison.OrdinalIgnoreCase);
        AssertNoDerivedOutputs(work, "game", checkZar: false);
    }

    [Fact]
    public void RedumpMode_PlainIso_StillRuns()
    {
        string work = CreateTempDir("xiso_redump_guard");
        string iso = CreateIso(work);

        int rc = Program.Main(["--wipe", "-y", iso]);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(iso)!, "game.wiped.xiso")));
    }

    private static void AssertNoDerivedOutputs(string directory, string stem, bool checkZar = true)
    {
        List<string> suffixes = [".wiped.xiso", ".trim.xiso", ".trim.wiped.xiso", ".filler", ".seed", ".skeleton.xiso"];
        if (checkZar) suffixes.Add(".zar");

        foreach (string suffix in suffixes)
        {
            Assert.False(File.Exists(Path.Combine(directory, stem + suffix)),
                $"guard should not have created {stem}{suffix}");
        }
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(string work)
    {
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string outDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(outDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }
}
