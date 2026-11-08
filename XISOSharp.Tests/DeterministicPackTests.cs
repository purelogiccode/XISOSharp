using System.Text;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <c>--deterministic</c> alias (BRF-11): directory, pack,
/// build-image and memory-source packing produce byte-identical images.
/// </summary>
[Collection("Sequential")]
public sealed class DeterministicPackTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public DeterministicPackTests()
    {
        Logger.Quiet = true;
    }

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string dir in _tempDirs)
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
                // best effort
            }
        }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_deterministic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSource()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "readme.txt"), "deterministic\n");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllBytes(Path.Combine(source, "sub", "data.bin"),
            Enumerable.Range(0, 5000).Select(static i => (byte)(i * 7)).ToArray());
        return source;
    }

    private static int Run(params string[] args) => RunResult(args).Exit;

    private static (int Exit, string Text, string Error) RunResult(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedError = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = error;
            int exit = Program.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedError;
        }
    }

    [Fact]
    public void PackFromDirectory_FileTimeZero_IsByteIdentical()
    {
        string source = CreateSource();
        string first = Path.Combine(TempDir(), "first.iso");
        string second = Path.Combine(TempDir(), "second.iso");

        Assert.Equal(0, XisoWriter.PackFromDirectory(source, first, fileTime: 0));
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, second, fileTime: 0));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Cli_PackDeterministic_IsByteIdentical()
    {
        string source = CreateSource();
        string first = Path.Combine(TempDir(), "first.iso");
        string second = Path.Combine(TempDir(), "second.iso");

        Assert.Equal(0, Run("--pack", source, first, "--deterministic"));
        Assert.Equal(0, Run("--pack", source, second, "--deterministic"));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Cli_CreateDeterministic_IsByteIdentical()
    {
        string source = CreateSource();
        string work = TempDir();
        string first = Path.Combine(work, "first.iso");
        string second = Path.Combine(work, "second.iso");

        Assert.Equal(0, Run("-c", source, first, "--deterministic"));
        Assert.Equal(0, Run("-c", source, second, "--deterministic"));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Cli_BuildImageDeterministic_IsByteIdentical()
    {
        string source = CreateSource();
        string first = Path.Combine(TempDir(), "first.iso");
        string second = Path.Combine(TempDir(), "second.iso");

        Assert.Equal(0, Run("build-image", source, first, "-m", "*:/", "--deterministic"));
        Assert.Equal(0, Run("build-image", source, second, "-m", "*:/", "--deterministic"));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void MemorySourceDeterministic_IsByteIdentical()
    {
        MemoryFilesystem files = new();
        using (Stream stream = files.CreateFile("readme.txt"))
        {
            stream.Write(Encoding.UTF8.GetBytes("memory source\n"));
        }

        using (Stream stream = files.CreateFile("sub/data.bin"))
        {
            stream.Write(Enumerable.Range(0, 3000).Select(static i => (byte)i).ToArray());
        }

        string first = Path.Combine(TempDir(), "first.iso");
        string second = Path.Combine(TempDir(), "second.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(files, "", first, fileTime: 0));
        Assert.Equal(0, XisoWriter.PackFromDirectory(files, "", second, fileTime: 0));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void RepackingAnImage_WithDeterministic_IsStable()
    {
        string source = CreateSource();
        string work = TempDir();
        string baseIso = Path.Combine(work, "base.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, baseIso, fileTime: 0));

        // Rewrite skips images that already carry the optimized tag; clear it so
        // this exercises an actual repack, then give each rewrite its own copy
        // (rewrite moves the source to <name>.old).
        using (FileStream fs = new(baseIso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength]);
        }

        string left = Path.Combine(work, "left.iso");
        string right = Path.Combine(work, "right.iso");
        File.Copy(baseIso, left);
        File.Copy(baseIso, right);
        string first = Path.Combine(TempDir(), "repack1.iso");
        string second = Path.Combine(TempDir(), "repack2.iso");
        (int firstExit, _, string firstError) = RunResult("-r", "-o", first, "--deterministic", left);
        Assert.True(firstExit == 0, firstError);
        (int secondExit, _, string secondError) = RunResult("-r", "-o", second, "--deterministic", right);
        Assert.True(secondExit == 0, secondError);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Deterministic_ConflictsWithExplicitFileTime()
    {
        string source = CreateSource();
        string output = Path.Combine(TempDir(), "out.iso");

        Assert.Equal(1, Run("--pack", source, output, "--deterministic", "--file-time", "now"));
        Assert.False(File.Exists(output));
    }
}
