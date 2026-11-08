using System.Security.Cryptography;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// BRF-06: collection verbs accept <c>--jobs 1..64</c>, preserve the sequential
/// output (including JSON array order) and stop at the same failures.
/// </summary>
[Collection("Sequential")]
public sealed class ParallelCollectionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly bool _savedRouteInfoToError;
    private readonly string _savedCwd;

    public ParallelCollectionTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        _savedRouteInfoToError = Logger.RouteInfoToError;
        Console.SetOut(_out);
        Console.SetError(_err);
        Logger.Out = _out;
        Logger.Error = _err;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;

        _savedCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(CreateTempDir("xiso_pcoll_cwd"));
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
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        Logger.RouteInfoToError = _savedRouteInfoToError;
        _out.Dispose();
        _err.Dispose();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void Checksum_JobsOutputMatchesSequentialTextAndJson()
    {
        string batch = CreateBatch("checksum", count: 3, withCorrupt: false);
        string[] images = Directory.GetFiles(batch, "*.iso").Order(StringComparer.Ordinal).ToArray();

        (int rc1, string out1, string err1) = Run([.. new[] { "checksum" }, .. images]);
        (int rc2, string out2, string err2) = Run([.. new[] { "checksum", "--jobs", "3" }, .. images]);
        Assert.Equal(rc1, rc2);
        Assert.Equal(out1, out2);
        Assert.Equal(err1, err2);

        (int jrc1, string jout1, _) = Run([.. new[] { "checksum", "--json" }, .. images]);
        (int jrc2, string jout2, _) = Run([.. new[] { "checksum", "--json", "--jobs", "3" }, .. images]);
        Assert.Equal(jrc1, jrc2);
        Assert.Equal(jout1, jout2);
        Assert.Contains("\"checksums\"", jout2, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksumFlag_JobsOutputMatchesSequential()
    {
        string batch = CreateBatch("checksumflag", count: 2, withCorrupt: false);
        string[] images = Directory.GetFiles(batch, "*.iso").Order(StringComparer.Ordinal).ToArray();

        (int rc1, string out1, string err1) = Run(["--checksum", .. images]);
        (int rc2, string out2, string err2) = Run(["--checksum", "--jobs", "2", .. images]);
        Assert.Equal(rc1, rc2);
        Assert.Equal(out1, out2);
        Assert.Equal(err1, err2);
    }

    [Fact]
    public void Hash_JobsOutputMatchesSequential()
    {
        string batch = CreateBatch("hash", count: 2, withCorrupt: false);
        string[] images = Directory.GetFiles(batch, "*.iso").Order(StringComparer.Ordinal).ToArray();

        (int rc1, string out1, string err1) = Run([.. new[] { "hash" }, .. images]);
        (int rc2, string out2, string err2) = Run([.. new[] { "hash", "--jobs", "2" }, .. images]);
        Assert.Equal(rc1, rc2);
        Assert.Equal(out1, out2);
        Assert.Equal(err1, err2);

        (_, string jout1, _) = Run([.. new[] { "hash", "--json" }, .. images]);
        (_, string jout2, _) = Run([.. new[] { "hash", "--json", "--jobs", "2" }, .. images]);
        Assert.Equal(jout1, jout2);
    }

    [Fact]
    public void ListTreeAudit_JobsOutputMatchesSequentialIncludingMixedFailures()
    {
        string batch = CreateBatch("collections", count: 3, withCorrupt: true);
        string[] images = Directory.GetFiles(batch, "*.iso").Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(4, images.Length);
        AssertMixedFlagOutput("--batch", batch, "-l");
        AssertMixedFlagOutput("--batch", batch, "-t");
        AssertMixedFlagOutput("--batch", batch, "-V");
    }

    [Fact]
    public void BatchExtract_JobsProducesIdenticalTrees()
    {
        string batch = CreateBatch("extract", count: 2, withCorrupt: false);
        string run1 = CreateTempDir("xiso_pcoll_extract_seq");
        string run2 = CreateTempDir("xiso_pcoll_extract_par");

        (int rc1, _, _) = RunIn(run1, ["--batch", batch, "-x", "-y"]);
        (int rc2, _, _) = RunIn(run2, ["--batch", batch, "-x", "-y", "--jobs", "2"]);

        Assert.Equal(0, rc1);
        Assert.Equal(rc1, rc2);
        Assert.Equal(HashTree(run1), HashTree(run2));
    }

    [Fact]
    public void BatchRewrite_JobsProducesIdenticalImages()
    {
        string source = CreateBatch("rewrite_src", count: 2, withCorrupt: false);
        string run1 = CreateTempDir("xiso_pcoll_rewrite_seq");
        string run2 = CreateTempDir("xiso_pcoll_rewrite_par");
        CopyBatch(source, run1);
        CopyBatch(source, run2);

        (int rc1, _, _) = Run(["--batch", run1, "-r", "-D", "-y"]);
        (int rc2, _, _) = Run(["--batch", run2, "-r", "-D", "-y", "--jobs", "2"]);

        Assert.Equal(0, rc1);
        Assert.Equal(rc1, rc2);
        Assert.Equal(HashTree(run1), HashTree(run2));
    }

    private void AssertMixedFlagOutput(string batchFlag, string batch, string modeFlag)
    {
        (int rc1, string out1, string err1) = Run([batchFlag, batch, modeFlag]);
        (int rc2, string out2, string err2) = Run([batchFlag, batch, modeFlag, "--jobs", "3"]);

        Assert.Equal(1, rc1);
        Assert.Equal(rc1, rc2);
        Assert.Equal(out1, out2);
        Assert.Equal(err1, err2);
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string[] args) => RunIn(null, args);

    private (int ExitCode, string StdOut, string StdErr) RunIn(string? workingDirectory, string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;
        Logger.TotalBytesAllIsos = 0;
        Logger.TotalFilesAllIsos = 0;

        string? previous = null;
        if (workingDirectory is not null)
        {
            previous = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(workingDirectory);
        }

        try
        {
            int rc = Program.Main(args);
            return (rc, _out.ToString(), _err.ToString());
        }
        finally
        {
            if (previous is not null)
            {
                Directory.SetCurrentDirectory(previous);
            }
        }
    }

    private string CreateBatch(string name, int count, bool withCorrupt)
    {
        string batch = CreateTempDir($"xiso_pcoll_{name}");
        for (int index = 1; index <= count; index++)
        {
            string source = CreateTempDir($"xiso_pcoll_{name}_src{index}");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "file.txt"), $"game {index}");
            File.WriteAllText(Path.Combine(source, "sub", "data.bin"),
                new string((char)('a' + index), 64 + index));
            string isoPath = Path.Combine(batch, $"game{index}.iso");
            Assert.Equal(0, XisoWriter.PackFromDirectory(source, isoPath, fileTime: 0));
        }

        if (withCorrupt)
        {
            File.WriteAllBytes(Path.Combine(batch, "zz-corrupt.iso"), RandomNumberGenerator.GetBytes(4096));
        }

        return batch;
    }

    private static void CopyBatch(string source, string destination)
    {
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            using FileStream stream = File.OpenRead(file);
            result[relative] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return result;
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
