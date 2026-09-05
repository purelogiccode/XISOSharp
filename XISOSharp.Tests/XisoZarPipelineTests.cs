using XISOSharp.Cli;
using ZARSharp;
using ZARSharp.Pipeline;

namespace XISOSharp.Tests;

/// <summary>
/// Step 5 pipeline coverage on the XISO side: <see cref="XisoZarchive.CreateZar"/>
/// progress reporting through the shared engine, and the CLI <c>--zar</c>
/// <c>--jobs</c> / <c>--policy</c> batch flags end to end via
/// <see cref="Program.Main"/>.
/// </summary>
[Collection("Sequential")]
public sealed class XisoZarPipelineTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly string _savedCwd;
    private readonly string _runDir;

    public XisoZarPipelineTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        _savedCwd = Directory.GetCurrentDirectory();
        _runDir = CreateTempDir("xiso_zp_rundir");
        Directory.SetCurrentDirectory(_runDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_savedCwd);
        }
        catch
        {
            // ignored
        }

        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedOut;
        Logger.Error = _savedErr;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _outCapture.Dispose();
        _errCapture.Dispose();

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
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

    private string CreateIso(string name, int fileCount)
    {
        var src = CreateTempDir("xiso_zp_src");
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(src, $"{name}{i}.txt"), $"payload {name} {i} " + new string('x', 5000));
        }

        var outDir = CreateTempDir("xiso_zp_iso");
        var result = XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private sealed class Collector : IProgress<ZarProgress>
    {
        private readonly Lock _gate = new();
        public readonly List<ZarProgress> Events = [];

        public void Report(ZarProgress value)
        {
            lock (_gate)
            {
                Events.Add(value);
            }
        }
    }

    [Fact]
    public void CreateZar_ReportsProgress_ToCompletion()
    {
        var iso = CreateIso("prog", 3);
        var zar = Path.Combine(CreateTempDir("xiso_zp_zar"), "prog.zar");

        var collector = new Collector();
        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true, progress: collector));

        Assert.NotEmpty(collector.Events);
        var last = collector.Events[^1];
        Assert.Equal(3, last.FilesTotal);
        Assert.Equal(3, last.FilesCompleted);
        Assert.Equal(1.0, last.Ratio);
        Assert.All(collector.Events, e => Assert.Equal(ZarOperation.Pack, e.Operation));
        var seen = collector.Events.Select(e => e.CurrentFile)
            .Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void Cli_Zar_PolicySkip_SecondRunSkips()
    {
        var iso = CreateIso("skip", 2);
        var zar = Path.Combine(CreateTempDir("xiso_zp_skip"), "skip.zar");

        Assert.Equal(0, Program.Main(["--zar", "-o", zar, iso]));
        var before = File.ReadAllBytes(zar);

        _outCapture.GetStringBuilder().Clear();
        Assert.Equal(0, Program.Main(["--zar", "--policy", "skip", "-o", zar, iso]));
        Assert.Equal(before, File.ReadAllBytes(zar));
        Assert.Contains("Skipping", _outCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Zar_PolicyOverwrite_Repacks()
    {
        var iso = CreateIso("over", 2);
        var zar = Path.Combine(CreateTempDir("xiso_zp_over"), "over.zar");

        Assert.Equal(0, Program.Main(["--zar", "-o", zar, iso]));
        _outCapture.GetStringBuilder().Clear();
        Assert.Equal(0, Program.Main(["--zar", "--policy", "overwrite", "-o", zar, iso]));
        Assert.Contains("ZAR written to", _outCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Zar_JobsParallel_PacksAllInputs()
    {
        var isoA = CreateIso("jobA", 2);
        var isoB = CreateIso("jobB", 2);

        Assert.Equal(0, Program.Main(["--zar", "--jobs", "2", "--policy", "overwrite", isoA, isoB]));
        Assert.True(File.Exists(Path.ChangeExtension(isoA, ".zar")));
        Assert.True(File.Exists(Path.ChangeExtension(isoB, ".zar")));
    }

    [Fact]
    public void Cli_Zar_BadPolicy_ReturnsOne()
    {
        var iso = CreateIso("badpol", 1);
        Assert.Equal(1, Program.Main(["--zar", "--policy", "bogus", iso]));
    }

    [Fact]
    public void Cli_Zar_BadJobs_ReturnsOne()
    {
        var iso = CreateIso("badjobs", 1);
        Assert.Equal(1, Program.Main(["--zar", "--jobs", "0", iso]));
    }
}
