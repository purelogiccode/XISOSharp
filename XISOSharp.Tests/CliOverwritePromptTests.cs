using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the CLI overwrite gate (<c>-y</c>/<c>--yes</c>, <c>-n</c>/<c>--no</c>,
/// interactive prompt), ported from <c>XboxKit/Helpers.cs::ConfirmOverwrite</c>.
/// The prompt I/O is injected so no console is needed.
/// </summary>
public sealed class CliOverwritePromptTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_overwrite_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateExistingFile()
    {
        string path = Path.Combine(CreateTempDir(), "out.iso");
        File.WriteAllText(path, "existing");
        return path;
    }

    [Fact]
    public void MissingFile_ReturnsTrueWithoutPrompting()
    {
        string missing = Path.Combine(CreateTempDir(), "nope.iso");
        StringWriter output = new();
        Assert.True(OverwritePrompt.ConfirmOverwrite(missing, assumeYes: false, assumeNo: false,
            new StringReader("n"), output));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void AssumeYes_ExistingFile_ReturnsTrueWithoutPrompting()
    {
        string path = CreateExistingFile();
        StringWriter output = new();
        // A "n" answer is queued: it must never be consumed.
        Assert.True(OverwritePrompt.ConfirmOverwrite(path, assumeYes: true, assumeNo: false,
            new StringReader("n"), output));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void AssumeNo_ExistingFile_ReturnsFalseWithError()
    {
        string path = CreateExistingFile();
        StringWriter output = new();
        Assert.False(OverwritePrompt.ConfirmOverwrite(path, assumeYes: false, assumeNo: true,
            new StringReader("y"), output));
        Assert.Contains($"[ERROR] File already exists: {path}", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData(" Yes ")]
    public void Prompt_AcceptsYesAnswers(string answer)
    {
        string path = CreateExistingFile();
        StringWriter output = new();
        Assert.True(OverwritePrompt.ConfirmOverwrite(path, assumeYes: false, assumeNo: false,
            new StringReader(answer), output));
        string text = output.ToString();
        Assert.Contains($"[WARNING] File already exists: {path}", text, StringComparison.Ordinal);
        Assert.Contains("Would you like to overwrite? (Y/N)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("N")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("maybe")]
    public void Prompt_RejectsNonYesAnswers(string answer)
    {
        string path = CreateExistingFile();
        StringWriter output = new();
        Assert.False(OverwritePrompt.ConfirmOverwrite(path, assumeYes: false, assumeNo: false,
            new StringReader(answer), output));
    }

    [Fact]
    public void Prompt_ClosedStdin_Declines()
    {
        string path = CreateExistingFile();
        StringReader reader = new(string.Empty);
        reader.ReadToEnd(); // subsequent ReadLine returns null, like redirected /dev/null
        Assert.False(OverwritePrompt.ConfirmOverwrite(path, assumeYes: false, assumeNo: false,
            reader, new StringWriter()));
    }

    [Fact]
    public void DeriveDefaultCsoPath_FileReplacesExtension()
    {
        string dir = CreateTempDir();
        Assert.Equal(Path.Combine(dir, "game.cso"),
            CisoWriter.DeriveDefaultCsoPath(Path.Combine(dir, "game.iso"), isDir: false));
        Assert.Equal(Path.Combine(dir, "game.cso"),
            CisoWriter.DeriveDefaultCsoPath(Path.Combine(dir, "game.xiso"), isDir: false));
    }

    [Fact]
    public void DeriveDefaultCsoPath_DirectoryMapsToSibling()
    {
        string dir = CreateTempDir();
        string src = Path.Combine(dir, "src");
        Directory.CreateDirectory(src);
        Assert.Equal(Path.Combine(dir, "src.cso"), CisoWriter.DeriveDefaultCsoPath(src, isDir: true));
    }

    [Fact]
    public void DeriveDefaultIsoPath_StripsCsoSuffix()
    {
        string dir = CreateTempDir();
        Assert.Equal(Path.Combine(dir, "game.iso"),
            CisoReader.DeriveDefaultIsoPath(Path.Combine(dir, "game.cso")));
        Assert.Equal(Path.Combine(dir, "game.iso"),
            CisoReader.DeriveDefaultIsoPath(Path.Combine(dir, "game.1.cso")));
    }
}
