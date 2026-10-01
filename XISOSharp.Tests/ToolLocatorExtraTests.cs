namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="ToolLocator"/>: file-name mapping, override
/// resolution, PATH lookup, and the <c>-v</c> probe, complementing <c>ToolLocatorTests</c>.
/// </summary>
[Collection("Sequential")]
public class ToolLocatorExtraTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly string? _origPath = Environment.GetEnvironmentVariable("PATH");

    /// <summary>Restores PATH and removes temporary directories.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _origPath);
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_toolloc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Verifies base names are rejected when null or empty.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetFileName_Invalid_Throws(string? baseName)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolLocator.GetFileName(baseName!));
    }

    /// <summary>Verifies the OS-aware extension convention.</summary>
    [Fact]
    public void GetFileName_IsOsAware()
    {
        string name = ToolLocator.GetFileName("extract-xiso");
        Assert.Equal(OperatingSystem.IsWindows() ? "extract-xiso.exe" : "extract-xiso", name);
    }

    /// <summary>Verifies base names are rejected when resolving by base name.</summary>
    [Fact]
    public void ResolveByBaseName_EmptyBase_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolLocator.ResolveByBaseName(null, string.Empty));
    }

    /// <summary>Verifies an existing override path wins over sibling/PATH lookup.</summary>
    [Fact]
    public void Resolve_ExistingOverride_ReturnsOverride()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "my-tool.bin");
        File.WriteAllText(file, "x");

        Assert.Equal(file, ToolLocator.Resolve(file, "my-tool.exe", "my-tool"));
    }

    /// <summary>Verifies a missing override falls through to a null result for an unknown tool.</summary>
    [Fact]
    public void Resolve_MissingOverrideUnknownTool_ReturnsNull()
    {
        string missing = Path.Combine(CreateTempDir(), "missing.bin");
        Assert.Null(ToolLocator.Resolve(missing, "xisosharp-no-such-tool-xyz.exe", "xisosharp-no-such-tool-xyz"));
    }

    /// <summary>Verifies an empty PATH yields no result.</summary>
    [Fact]
    public void FindOnPath_EmptyFileName_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolLocator.FindOnPath(string.Empty));
    }

    /// <summary>Verifies a missing executable yields null.</summary>
    [Fact]
    public void FindOnPath_Missing_ReturnsNull()
    {
        Assert.Null(ToolLocator.FindOnPath("xisosharp-no-such-tool-xyz"));
    }

    /// <summary>Verifies executables in PATH directories are found.</summary>
    [Fact]
    public void FindOnPath_FindsFileOnPath()
    {
        string dir = CreateTempDir();
        string name = OperatingSystem.IsWindows() ? "xisosharp-tooltest-xyz.exe" : "xisosharp-tooltest-xyz";
        string file = Path.Combine(dir, name);
        File.WriteAllText(file, string.Empty);
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _origPath);

        Assert.Equal(file, ToolLocator.FindOnPath(name));
    }

    /// <summary>Verifies blank probe paths yield null.</summary>
    [Fact]
    public async Task ProbeVersionAsync_BlankPath_ReturnsNull()
    {
        Assert.Null(await ToolLocator.ProbeVersionAsync("   "));
    }

    /// <summary>Verifies missing tools yield null instead of throwing.</summary>
    [Fact]
    public async Task ProbeVersionAsync_MissingFile_ReturnsNull()
    {
        string missing = Path.Combine(CreateTempDir(), "missing-tool-xyz");
        Assert.Null(await ToolLocator.ProbeVersionAsync(missing));
    }

    /// <summary>Verifies a pre-canceled token yields null instead of throwing.</summary>
    [Fact]
    public async Task ProbeVersionAsync_Canceled_ReturnsNull()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        string cliPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "XISOSharp.Cli.exe" : "XISOSharp.Cli");
        Assert.Null(await ToolLocator.ProbeVersionAsync(cliPath, cancellationToken: cts.Token));
    }

    /// <summary>Verifies probing the shipped CLI returns its version banner.</summary>
    [Fact]
    public async Task ProbeVersionAsync_CliBinary_ReturnsBanner()
    {
        string cliPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "XISOSharp.Cli.exe" : "XISOSharp.Cli");
        Assert.True(File.Exists(cliPath), $"expected CLI binary at {cliPath}");

        string? version = await ToolLocator.ProbeVersionAsync(cliPath);

        Assert.NotNull(version);
        Assert.StartsWith("XISOSharp v", version, StringComparison.Ordinal);
    }
}
