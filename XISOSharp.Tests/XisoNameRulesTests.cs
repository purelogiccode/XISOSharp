using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the Xbox 42-character filename policy and the actionable
/// Latin-1 name errors (BRF-10).
/// </summary>
[Collection("Sequential")]
public sealed class XisoNameRulesTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.Warned = false;
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_names_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string LongName(int length) => new string('n', length - 4) + ".txt";

    private static (int Exit, string Error) Run(params string[] args)
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
            return (exit, error.ToString());
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedError;
        }
    }

    [Fact]
    public void GetComponentError_AcceptsFortyTwoAndRejectsFortyThree()
    {
        Assert.Null(XisoNameRules.GetComponentError(new string('a', 42)));
        string? error = XisoNameRules.GetComponentError(new string('a', 43));
        Assert.NotNull(error);
        Assert.Contains("42", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GetComponentError_AcceptsPlusBangSpacesAndLatin1()
    {
        Assert.Null(XisoNameRules.GetComponentError("a+b! c\u00e9.txt"));
        Assert.Null(XisoNameRules.GetComponentError("na\u00efve.txt"));
    }

    [Fact]
    public void GetComponentError_RejectsNonLatin1()
    {
        string? error = XisoNameRules.GetComponentError("日本語.txt");
        Assert.NotNull(error);
        Assert.Contains("Latin-1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackFromDirectory_LongName_WarnsByDefault()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, LongName(43)), "long");
        string output = Path.Combine(TempDir(), "out.iso");

        (int exit, string error) = Run("-c", source, output);

        Assert.Equal(0, exit);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void PackFromDirectory_LongName_StrictFailsBeforeWriting()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, LongName(43)), "long");
        string output = Path.Combine(TempDir(), "out.iso");

        (int exit, string error) = Run("-c", source, output, "--strict-names");

        Assert.Equal(1, exit);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.Contains(LongName(43), error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackFromDirectory_NonLatin1Name_FailsWithActionableError()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "日本語.txt"), "unicode");
        string output = Path.Combine(TempDir(), "out.iso");

        (int exit, string error) = Run("-c", source, output);

        Assert.Equal(1, exit);
        Assert.Contains("Latin-1", error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackFromDirectory_PlusBangSpaceAndLatin1_RoundTrip()
    {
        string source = TempDir();
        string longButValid = new string('a', 38) + ".txt";
        File.WriteAllText(Path.Combine(source, "a+b! c\u00e9.txt"), "special");
        File.WriteAllText(Path.Combine(source, "na\u00efve.txt"), "latin");
        File.WriteAllText(Path.Combine(source, longButValid), "forty-two");
        string output = Path.Combine(TempDir(), "out.iso");

        (int exit, string error) = Run("-c", source, output, "--strict-names");
        Assert.True(exit == 0, error);

        IReadOnlyList<string> names = XisoReader.ListDirectoryFlat(output);
        Assert.Contains("a+b! c\u00e9.txt", names, StringComparer.Ordinal);
        Assert.Contains("na\u00efve.txt", names, StringComparer.Ordinal);
        Assert.Contains(longButValid, names, StringComparer.Ordinal);
    }

    [Fact]
    public void ApplyStrictNames_LongSourceComponent_Fails()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, LongName(43)), "long");
        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        string work = TempDir();
        string skeleton = Path.Combine(work, "game.skeleton.xiso");
        Assert.True(XisoSkeleton.Petrify(iso, skeleton, quiet: true));
        string output = Path.Combine(work, "applied.iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output, strictNames: true));

        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void CopyInStrictNames_LongInternalComponent_FailsBeforeBackup()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        string host = Path.Combine(TempDir(), "payload.bin");
        File.WriteAllBytes(host, [1, 2, 3]);

        (int exit, string error) = Run("--strict-names", "--copy-in", iso, host, "/" + LongName(43));

        Assert.Equal(1, exit);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.False(File.Exists(iso + ".old"));
    }

    [Fact]
    public void PetrifyStrictNames_LongImageComponent_Fails()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, LongName(43)), "long");
        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        string skeleton = Path.Combine(TempDir(), "game.skeleton.xiso");

        (int exit, string error) = Run("--strict-names", "--petrify", "-o", skeleton, iso);

        Assert.Equal(1, exit);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.False(File.Exists(skeleton));
    }

    [Fact]
    public void BuildImageStrictNames_LongMappedComponent_Fails()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "payload.bin"), "data");
        string output = Path.Combine(TempDir(), "out.iso");

        (int exit, string error) = Run("build-image", source, output, "-m",
            "payload.bin:/" + LongName(43), "--strict-names");

        Assert.Equal(1, exit);
        Assert.Contains("42", error, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}
