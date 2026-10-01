#if NET10_0_OR_GREATER
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="CliLocator"/>: OS-aware file name and version
/// metadata fallbacks for the resolved CLI binary.
/// </summary>
public class CliLocatorTests
{
    /// <summary>Verifies the CLI file name follows the OS convention.</summary>
    [Fact]
    public void CliFileName_IsOsAware()
    {
        Assert.Equal(XISOSharp.ToolLocator.GetFileName("XISOSharp"), CliLocator.CliFileName);
    }

    /// <summary>Verifies a missing binary yields no product version.</summary>
    [Fact]
    public void ProductVersion_MissingFile_ReturnsNull()
    {
        Assert.Null(CliLocator.ProductVersion(Path.Combine(Path.GetTempPath(), "no-such-cli-xyz")));
    }

    /// <summary>Verifies a missing binary falls back to the generic product label.</summary>
    [Fact]
    public void ProductLabel_MissingFile_ReturnsFallback()
    {
        Assert.Equal("XISOSharp CLI", CliLocator.ProductLabel(Path.Combine(Path.GetTempPath(), "no-such-cli-xyz")));
    }

    /// <summary>Verifies the shipped CLI binary's metadata produces a clean label.</summary>
    [Fact]
    public void ProductVersion_CliBinary_IsClean()
    {
        string cliPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "XISOSharp.Cli.exe" : "XISOSharp.Cli");
        Assert.True(File.Exists(cliPath), $"expected CLI binary at {cliPath}");

        string? version = CliLocator.ProductVersion(cliPath);
        if (version is not null)
        {
            Assert.NotEmpty(version);
            Assert.DoesNotContain("+", version, StringComparison.Ordinal);
        }

        string label = CliLocator.ProductLabel(cliPath);
        Assert.StartsWith("XISOSharp ", label, StringComparison.Ordinal);
    }

    /// <summary>Verifies resolving a blank override never returns a blank path.</summary>
    [Fact]
    public void Resolve_BlankOverride_DoesNotReturnBlank()
    {
        string? resolved = CliLocator.Resolve("   ");
        Assert.True(resolved is null || resolved.Length > 0);
    }
}
#endif
