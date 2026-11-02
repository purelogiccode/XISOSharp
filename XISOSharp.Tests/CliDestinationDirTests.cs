using System.Security.Cryptography;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Edge-case tests for the extract destination (<c>-d</c>) the way batch
/// scripts pass it (upstream #61): trailing backslashes, UNC paths, and
/// directories with spaces — plus the misplaced-flag error that replaces the
/// old <c>open error: -d</c> confusion when the flag trails the ISO.
/// CLI runs go through <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class CliDestinationDirTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly string _savedCwd;
    private readonly string _runDir;

    public CliDestinationDirTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedLoggerOut = Logger.Out;
        _savedLoggerError = Logger.Error;
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        // Any accidental default (non -d) output lands in temp, never in the
        // test runner's directory.
        _savedCwd = Directory.GetCurrentDirectory();
        _runDir = CreateTempDir("xiso_d rundir");
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
        Logger.Out = _savedLoggerOut;
        Logger.Error = _savedLoggerError;
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        _outCapture.Dispose();
        _errCapture.Dispose();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceTree()
    {
        string root = CreateTempDir("xiso_d_src");
        Directory.CreateDirectory(Path.Combine(root, "sub dir"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub dir", "b.txt"), new string('B', 5000));
        byte[] payload = new byte[20000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(Path.Combine(root, "data.bin"), payload);
        return root;
    }

    private string CreateIso(string srcDir, string isoName, string? outputDir = null)
    {
        string dir = outputDir ?? CreateTempDir("xiso_d_iso");
        int result = XisoWriter.CreateXiso(srcDir, dir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(XisoPaths.TrimTrailingSeparators(dir), isoName), created);
        return created!;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using SHA256 sha = SHA256.Create();
            using FileStream fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    [Fact]
    public void Extract_TrailingSeparator_SameTreeAsControl()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string control = CreateTempDir("xiso_d_control");
        string trailed = CreateTempDir("xiso_d_trailed") + Path.DirectorySeparatorChar;

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, trailed));
        Assert.Equal(HashTree(src), HashTree(control));
        Assert.Equal(HashTree(control), HashTree(XisoPaths.TrimTrailingSeparators(trailed)));
    }

    [Fact]
    public void Extract_DoubledTrailingSeparators_SameTreeAsControl()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string control = CreateTempDir("xiso_d_control2");
        string doubled = CreateTempDir("xiso_d_doubled")
                         + new string(Path.DirectorySeparatorChar, 2);

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, doubled));
        Assert.Equal(HashTree(control), HashTree(XisoPaths.TrimTrailingSeparators(doubled)));
    }

    [Fact]
    public void Extract_DestinationWithSpaces_MatchesControl()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string control = CreateTempDir("xiso_d_control3");
        string spaced = Path.Combine(CreateTempDir("xiso_d_parent"), "my games out");
        _tempDirs.Add(spaced);

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, spaced));
        Assert.Equal(HashTree(control), HashTree(spaced));
    }

    [Fact]
    public void CreateIso_OutputDirWithTrailingSeparator_NamesOutputNormally()
    {
        string src = CreateSourceTree();
        string isoDir = CreateTempDir("xiso_d_create") + Path.DirectorySeparatorChar;
        string isoPath = CreateIso(src, "game.iso", isoDir);
        Assert.True(File.Exists(isoPath));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, CreateTempDir("xiso_d_verify")));
    }

    [Fact]
    public void Extract_EmptyDestination_ThrowsArgumentException()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string cwd = Directory.GetCurrentDirectory();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => XisoReader.UnpackImage(isoPath, ""));
        Assert.Contains("must not be empty", ex.Message, StringComparison.Ordinal);
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Extract_DestinationIsExistingFile_ThrowsIOException()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string blocker = Path.Combine(CreateTempDir("xiso_d_blocker"), "file");
        File.WriteAllText(blocker, "in the way");
        string cwd = Directory.GetCurrentDirectory();

        Assert.Throws<IOException>(() => XisoReader.UnpackImage(isoPath, blocker));
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [WindowsOnlyFact]
    public void Extract_UnreachableUnc_FailsCleanly()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string cwd = Directory.GetCurrentDirectory();

        Assert.Throws<IOException>(() =>
            XisoReader.UnpackImage(isoPath, @"\\xiso-sharp-invalid-host\share\out"));
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [WindowsOnlyFact]
    public void Extract_DevicePathPrefix_MatchesControl()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string control = CreateTempDir("xiso_d_control4");
        string extended = @"\\?\" + CreateTempDir("xiso_d_extended");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, extended));
        Assert.Equal(HashTree(control), HashTree(extended));
    }

    [Fact]
    public void Cli_Extract_DFlagFirst_TrailingSepAndSpaces_ExitZero()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string dest = Path.Combine(CreateTempDir("xiso_d_cli"), "my games out")
                      + Path.DirectorySeparatorChar;

        int rc = Program.Main(["-x", "-d", dest, isoPath]);

        Assert.Equal(0, rc);
        Assert.Equal(HashTree(src), HashTree(XisoPaths.TrimTrailingSeparators(dest)));
    }

    [Fact]
    public void Cli_Extract_DFlagAfterIso_ReportsMisplacedFlag()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string dest = Path.Combine(CreateTempDir("xiso_d_cli2"), "new");

        // Exact upstream #61 shape: the flag trails the positional.
        int rc = Program.Main([isoPath, "-d", dest]);

        Assert.Equal(1, rc);
        string err = _errCapture.ToString();
        Assert.Contains("-d", err, StringComparison.Ordinal);
        Assert.Contains("must come before", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dest));
    }

    [WindowsOnlyFact]
    public void Cli_Extract_UnreachableUnc_ExitOne()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");

        int rc = Program.Main(["-x", "-d", @"\\xiso-sharp-invalid-host\share\out", isoPath]);

        Assert.Equal(1, rc);
        Assert.NotEmpty(_errCapture.ToString());
    }

    [Fact]
    public void Cli_Extract_BareDashDIsFlagErrorEvenWhenFileExists()
    {
        // CLI-025: no existence bypass — a bare "-d" in a positional slot is
        // a misplaced-flag error even when a file with that name exists on
        // disk (use ./-d or the absolute path for such files).
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        File.WriteAllText(Path.Combine(_runDir, "-d"), "decoy, not an image");

        int rc = Program.Main([isoPath, "-d"]);

        Assert.Equal(1, rc);
        Assert.Contains("must come before", _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Extract_LiteralDashDFile_StillTreatedAsFile()
    {
        // The absolute spelling of a file literally named like a flag keeps
        // working: it falls through to the normal (failing) image open
        // instead of the flag error.
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string decoy = Path.Combine(_runDir, "-d");
        File.WriteAllText(decoy, "decoy, not an image");

        int rc = Program.Main([isoPath, decoy]);

        Assert.Equal(1, rc);
        Assert.DoesNotContain("must come before", _errCapture.ToString(), StringComparison.Ordinal);
    }
}
