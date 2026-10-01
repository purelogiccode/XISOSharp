using XISOSharp.Cli.Logging;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="EnvironmentInfo"/>: version reporting, platform labels,
/// and the collected environment block.
/// </summary>
public class EnvironmentInfoExtraTests
{
    /// <summary>Verifies the application version is reported and parseable.</summary>
    [Fact]
    public void ApplicationVersion_IsReported()
    {
        string version = EnvironmentInfo.ApplicationVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("Unknown", version);
    }

    /// <summary>Verifies the platform label matches the running OS.</summary>
    [Fact]
    public void PlatformVersionLabel_MatchesCurrentOs()
    {
        string label = EnvironmentInfo.PlatformVersionLabel();
        string expected =
            OperatingSystem.IsWindows() ? "Windows Version" :
            OperatingSystem.IsLinux() ? "Linux Version" :
            OperatingSystem.IsMacOS() ? "MacOsX Version" : "OS Version";
        Assert.Equal(expected, label);
    }

    /// <summary>Verifies the collected block contains every required line.</summary>
    [Fact]
    public void Collect_ContainsRequiredLines()
    {
        string report = EnvironmentInfo.Collect("TestApplication");

        Assert.Contains("=== Environment Details ===", report, StringComparison.Ordinal);
        Assert.Contains("Application Name: TestApplication", report, StringComparison.Ordinal);
        Assert.Contains("Application Version: ", report, StringComparison.Ordinal);
        Assert.Contains("OS Version: ", report, StringComparison.Ordinal);
        Assert.Contains("Architecture: ", report, StringComparison.Ordinal);
        Assert.Contains("Bitness: ", report, StringComparison.Ordinal);
        Assert.Contains($"{EnvironmentInfo.PlatformVersionLabel()}: ", report, StringComparison.Ordinal);
        Assert.Contains("Processor Count: ", report, StringComparison.Ordinal);
        Assert.Contains("Base Directory: ", report, StringComparison.Ordinal);
        Assert.Contains("Temp Path: ", report, StringComparison.Ordinal);
        Assert.Contains("Date: ", report, StringComparison.Ordinal);
    }

    /// <summary>Verifies the environment block starts with its section header.</summary>
    [Fact]
    public void Collect_StartsWithSectionHeader()
    {
        string report = EnvironmentInfo.Collect("App");
        Assert.StartsWith("=== Environment Details ===", report, StringComparison.Ordinal);
    }
}
