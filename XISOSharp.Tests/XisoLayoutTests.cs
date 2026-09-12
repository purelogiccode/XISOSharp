using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the layout-inspection CLI verbs <c>--sector-layout</c>,
/// <c>--ranges</c>, and <c>--is-optimized</c> (library APIs
/// <see cref="XisoReader.GetSectorLayout"/>, <see cref="XisoRanges.GetXisoRanges(FileStream, long, bool)"/>,
/// and <see cref="XisoReader.IsOptimizedImage(Stream, int?)"/> surfaced for users).
/// CLI runs go through <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class XisoLayoutTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    public XisoLayoutTests()
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

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateLayoutIso()
    {
        string src = CreateTempDir("xiso_layout_src");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "layout me");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "nested.txt"), "nested");
        string outDir = CreateTempDir("xiso_layout_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void Cli_SectorLayout_EndToEnd()
    {
        string iso = CreateLayoutIso();

        Assert.Equal(0, Program.Main(["--sector-layout", iso]));
        string output = _outCapture.ToString();
        Assert.Contains("Sector layout:", output, StringComparison.Ordinal);
        Assert.Contains("Format:", output, StringComparison.Ordinal);
        Assert.Contains("Root dir:", output, StringComparison.Ordinal);
        Assert.Contains("Total sectors:", output, StringComparison.Ordinal);
        Assert.Contains("/readme.txt", output, StringComparison.Ordinal);
        Assert.Contains("Used ranges:", output, StringComparison.Ordinal);
        Assert.Contains("Free ranges:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_SectorLayout_UsageErrors()
    {
        string iso = CreateLayoutIso();

        Assert.Equal(1, Program.Main(["--sector-layout"]));
        Assert.Equal(1, Program.Main(["--sector-layout", iso, "extra"]));
        Assert.Equal(1, Program.Main(["--sector-layout", Path.Combine(Path.GetTempPath(), "no_such.iso")]));
    }

    [Fact]
    public void Cli_Ranges_EndToEnd()
    {
        string iso = CreateLayoutIso();

        Assert.Equal(0, Program.Main(["--ranges", iso]));
        string output = _outCapture.ToString();
        Assert.Contains("Sector ranges:", output, StringComparison.Ordinal);
        Assert.Contains("System (", output, StringComparison.Ordinal);
        Assert.Contains("Files (", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Ranges_UsageErrors()
    {
        string iso = CreateLayoutIso();

        Assert.Equal(1, Program.Main(["--ranges"]));
        Assert.Equal(1, Program.Main(["--ranges", iso, "extra"]));
        Assert.Equal(1, Program.Main(["--ranges", Path.Combine(Path.GetTempPath(), "no_such.iso")]));
    }

    [Fact]
    public void Cli_IsOptimized_EndToEnd()
    {
        // Library-created images carry the optimized tag.
        string iso = CreateLayoutIso();

        Assert.Equal(0, Program.Main(["--is-optimized", iso]));
        Assert.Contains("optimized", _outCapture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not optimized", _outCapture.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, Program.Main(["--is-optimized"]));
        Assert.Equal(1, Program.Main(["--is-optimized", iso, "extra"]));
        Assert.Equal(1, Program.Main(["--is-optimized", Path.Combine(Path.GetTempPath(), "no_such.iso")]));
    }

    [Fact]
    public void Cli_LayoutVerbs_RejectCombinations()
    {
        string iso = CreateLayoutIso();
        string batch = CreateTempDir("xiso_layout_batch");

        // Classic modes stay mutually exclusive.
        Assert.Equal(1, Program.Main(["--sector-layout", "--ranges", iso]));
        Assert.Equal(1, Program.Main(["--ranges", "--is-optimized", iso]));
        Assert.Equal(1, Program.Main(["-l", "--sector-layout", iso]));

        // --batch and --skip-sectors stay rejected (no offset/batch support),
        // except --is-optimized which probes through skipSectors.
        Assert.Equal(1, Program.Main(["--sector-layout", "--batch", batch]));
        Assert.Equal(1, Program.Main(["--sector-layout", "--skip-sectors", "1", iso]));
        Assert.Equal(1, Program.Main(["--ranges", "--skip-sectors", "1", iso]));
        Assert.Equal(0, Program.Main(["--is-optimized", "--skip-sectors", "0", iso]));
    }

    [Fact]
    public void Cli_LayoutVerbs_MisplacedAfterFilename_IsRefused()
    {
        string iso = CreateLayoutIso();

        Assert.Equal(1, Program.Main([iso, "--ranges"]));
        string err = _errCapture.ToString();
        Assert.Contains("--ranges", err, StringComparison.Ordinal);
        Assert.Contains("must come before", err, StringComparison.Ordinal);
        Assert.DoesNotContain("open error", err, StringComparison.Ordinal);
    }
}
