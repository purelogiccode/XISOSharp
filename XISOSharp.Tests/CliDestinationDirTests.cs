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
    private readonly string _savedCwd;
    private readonly string _runDir;

    public CliDestinationDirTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
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
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceTree()
    {
        var root = CreateTempDir("xiso_d_src");
        Directory.CreateDirectory(Path.Combine(root, "sub dir"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub dir", "b.txt"), new string('B', 5000));
        var payload = new byte[20000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(Path.Combine(root, "data.bin"), payload);
        return root;
    }

    private string CreateIso(string srcDir, string isoName, string? outputDir = null)
    {
        var dir = outputDir ?? CreateTempDir("xiso_d_iso");
        var result = XisoWriter.CreateXiso(srcDir, dir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(Path.Combine(XisoPaths.TrimTrailingSeparators(dir), isoName), created);
        return created!;
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

    [Fact]
    public void Extract_TrailingSeparator_SameTreeAsControl()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var control = CreateTempDir("xiso_d_control");
        var trailed = CreateTempDir("xiso_d_trailed") + Path.DirectorySeparatorChar;

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, trailed));
        Assert.Equal(HashTree(src), HashTree(control));
        Assert.Equal(HashTree(control), HashTree(XisoPaths.TrimTrailingSeparators(trailed)));
    }

    [Fact]
    public void Extract_DoubledTrailingSeparators_SameTreeAsControl()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var control = CreateTempDir("xiso_d_control2");
        var doubled = CreateTempDir("xiso_d_doubled")
                      + new string(Path.DirectorySeparatorChar, 2);

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, doubled));
        Assert.Equal(HashTree(control), HashTree(XisoPaths.TrimTrailingSeparators(doubled)));
    }

    [Fact]
    public void Extract_DestinationWithSpaces_MatchesControl()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var control = CreateTempDir("xiso_d_control3");
        var spaced = Path.Combine(CreateTempDir("xiso_d_parent"), "my games out");
        _tempDirs.Add(spaced);

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, spaced));
        Assert.Equal(HashTree(control), HashTree(spaced));
    }

    [Fact]
    public void CreateIso_OutputDirWithTrailingSeparator_NamesOutputNormally()
    {
        var src = CreateSourceTree();
        var isoDir = CreateTempDir("xiso_d_create") + Path.DirectorySeparatorChar;
        var isoPath = CreateIso(src, "game.iso", isoDir);
        Assert.True(File.Exists(isoPath));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, CreateTempDir("xiso_d_verify")));
    }

    [Fact]
    public void Extract_EmptyDestination_ThrowsArgumentException()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var cwd = Directory.GetCurrentDirectory();

        var ex = Assert.Throws<ArgumentException>(() => XisoReader.UnpackImage(isoPath, ""));
        Assert.Contains("must not be empty", ex.Message, StringComparison.Ordinal);
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Extract_DestinationIsExistingFile_ThrowsIOException()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var blocker = Path.Combine(CreateTempDir("xiso_d_blocker"), "file");
        File.WriteAllText(blocker, "in the way");
        var cwd = Directory.GetCurrentDirectory();

        Assert.Throws<IOException>(() => XisoReader.UnpackImage(isoPath, blocker));
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Extract_UnreachableUnc_FailsCleanly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var cwd = Directory.GetCurrentDirectory();

        Assert.Throws<IOException>(() =>
            XisoReader.UnpackImage(isoPath, @"\\xiso-sharp-invalid-host\share\out"));
        Assert.Equal(cwd, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Extract_DevicePathPrefix_MatchesControl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var control = CreateTempDir("xiso_d_control4");
        var extended = @"\\?\" + CreateTempDir("xiso_d_extended");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, extended));
        Assert.Equal(HashTree(control), HashTree(extended));
    }

    [Fact]
    public void Cli_Extract_DFlagFirst_TrailingSepAndSpaces_ExitZero()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = Path.Combine(CreateTempDir("xiso_d_cli"), "my games out")
                   + Path.DirectorySeparatorChar;

        var rc = Program.Main(["-x", "-d", dest, isoPath]);

        Assert.Equal(0, rc);
        Assert.Equal(HashTree(src), HashTree(XisoPaths.TrimTrailingSeparators(dest)));
    }

    [Fact]
    public void Cli_Extract_DFlagAfterIso_ReportsMisplacedFlag()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = Path.Combine(CreateTempDir("xiso_d_cli2"), "new");

        // Exact upstream #61 shape: the flag trails the positional.
        var rc = Program.Main([isoPath, "-d", dest]);

        Assert.Equal(1, rc);
        var err = _errCapture.ToString();
        Assert.Contains("-d", err, StringComparison.Ordinal);
        Assert.Contains("must come before", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void Cli_Extract_UnreachableUnc_ExitOne()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");

        var rc = Program.Main(["-x", "-d", @"\\xiso-sharp-invalid-host\share\out", isoPath]);

        Assert.Equal(1, rc);
        Assert.NotEmpty(_errCapture.ToString());
    }

    [Fact]
    public void Cli_Extract_LiteralDashDFile_StillTreatedAsFile()
    {
        // A file literally named like a flag keeps working: existence on disk
        // wins over the misplaced-flag diagnostic, so this falls through to
        // the normal (failing) image open instead of the flag error.
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        File.WriteAllText(Path.Combine(_runDir, "-d"), "decoy, not an image");

        var rc = Program.Main([isoPath, "-d"]);

        Assert.Equal(1, rc);
        Assert.DoesNotContain("must come before", _errCapture.ToString(), StringComparison.Ordinal);
    }
}