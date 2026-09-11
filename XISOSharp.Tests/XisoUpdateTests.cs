using System.Runtime.InteropServices;
using System.Text.Json;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the GitHub update check (<see cref="UpdateChecker"/>): version
/// comparison, platform RID mapping, release-asset picking, cache round-trip,
/// and the no-network opt-outs. The live HTTP path is never exercised here.
/// </summary>
[Collection("Sequential")]
public class XisoUpdateTests : IDisposable
{
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerError;
    private readonly List<string> _tempFiles = [];

    public XisoUpdateTests()
    {
        _savedErr = Console.Error;
        _savedLoggerError = Logger.Error;
        Console.SetError(_errCapture);
        Logger.Error = _errCapture;
    }

    public void Dispose()
    {
        Console.SetError(_savedErr);
        Logger.Error = _savedLoggerError;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _errCapture.Dispose();
        foreach (string file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("1.0.2", "1.0.1", false)]
    [InlineData("1.0.2-alpha.0.5", "1.0.1", false)] // dev build ahead of the release
    [InlineData("1.0.1", "v1.0.2", true)] // v-prefixed tags accepted
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("1.0.1-alpha.0.1", "1.0.1", true)] // finished release beats local prerelease
    [InlineData("1.0.1+abc123", "1.0.1", false)] // build metadata ignored
    [InlineData("1.9.0", "1.10.0", true)] // numeric, not lexicographic
    [InlineData("Unknown", "1.0.1", false)]
    [InlineData("1.0.1", "garbage", false)]
    [InlineData("1.0.1", "", false)]
    [InlineData(null, "1.0.1", false)]
    [InlineData("1.0.1", null, false)]
    public void IsUpdateAvailable_Matrix(string? local, string? remote, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsUpdateAvailable(local, remote));

    [Theory]
    [InlineData("Windows", Architecture.X64, "win-x64")]
    [InlineData("Windows", Architecture.Arm64, "win-arm64")]
    [InlineData("Windows", Architecture.X86, null)]
    [InlineData("Linux", Architecture.X64, "linux-x64")]
    [InlineData("Linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("OSX", Architecture.X64, "MacOsX-x64")]
    [InlineData("OSX", Architecture.Arm64, "MacOsX-arm64")]
    [InlineData("FreeBSD", Architecture.X64, null)]
    public void MapRid_Matrix(string platform, Architecture architecture, string? expected) =>
        Assert.Equal(expected, UpdateChecker.MapRid(OSPlatform.Create(platform), architecture));

    [Fact]
    public void BuildAssetName_FollowsReleaseConvention() =>
        Assert.Equal("release_1.0.0_win-x64.zip", UpdateChecker.BuildAssetName("1.0.0", "win-x64"));

    [Fact]
    public void BuildAssetName_StripsLeadingV() =>
        Assert.Equal("release_1.0.0_MacOsX-arm64.zip", UpdateChecker.BuildAssetName("v1.0.0", "MacOsX-arm64"));

    private static JsonElement ReleasePayload() =>
        JsonDocument.Parse("""
            {
              "tag_name": "1.0.2",
              "html_url": "https://github.com/purelogiccode/XISOSharp/releases/tag/1.0.2",
              "assets": [
                { "name": "release_1.0.2_win-x64.zip",
                  "browser_download_url": "https://github.com/purelogiccode/XISOSharp/releases/download/1.0.2/release_1.0.2_win-x64.zip" },
                { "name": "release_1.0.2_linux-x64.zip",
                  "browser_download_url": "https://github.com/purelogiccode/XISOSharp/releases/download/1.0.2/release_1.0.2_linux-x64.zip" }
              ]
            }
            """).RootElement;

    [Fact]
    public void FindAssetUrl_Match_ReturnsDownloadUrl()
    {
        string? url = UpdateChecker.FindAssetUrl(ReleasePayload(), "1.0.2", "win-x64");

        Assert.Equal(
            "https://github.com/purelogiccode/XISOSharp/releases/download/1.0.2/release_1.0.2_win-x64.zip", url);
    }

    [Fact]
    public void FindAssetUrl_MissingAsset_ReturnsNull() =>
        Assert.Null(UpdateChecker.FindAssetUrl(ReleasePayload(), "1.0.2", "MacOsX-arm64"));

    [Fact]
    public void FindAssetUrl_MalformedPayload_ReturnsNull()
    {
        JsonElement empty = JsonDocument.Parse("{}").RootElement;
        Assert.Null(UpdateChecker.FindAssetUrl(empty, "1.0.2", "win-x64"));
    }

    [Fact]
    public void Cache_RoundTrip_PreservesFields()
    {
        string path = TempFile();
        UpdateChecker.ReleaseInfo written = new(
            new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc),
            "1.0.2",
            "https://github.com/purelogiccode/XISOSharp/releases/tag/1.0.2",
            "release_1.0.2_win-x64.zip",
            "https://example.com/release_1.0.2_win-x64.zip");

        UpdateChecker.WriteCache(path, written);
        UpdateChecker.ReleaseInfo? read = UpdateChecker.ReadCache(path);

        Assert.NotNull(read);
        Assert.Equal(written.CheckedUtc, read.CheckedUtc);
        Assert.Equal("1.0.2", read.Tag);
        Assert.Equal(written.Url, read.Url);
        Assert.Equal("release_1.0.2_win-x64.zip", read.AssetName);
        Assert.Equal(written.AssetUrl, read.AssetUrl);
    }

    [Fact]
    public void Cache_MissingOrCorrupt_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ReadCache(TempFile()));
        string corrupt = TempFile();
        File.WriteAllText(corrupt, "{not json");
        Assert.Null(UpdateChecker.ReadCache(corrupt));
    }

    [Fact]
    public void CheckForUpdates_EnvDisabled_WritesNothing()
    {
        string? saved = Environment.GetEnvironmentVariable(UpdateChecker.DisableEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(UpdateChecker.DisableEnvVar, "1");
            UpdateChecker.CheckForUpdates(["--sector-layout", "game.iso"]);
            Assert.Equal(string.Empty, _errCapture.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateChecker.DisableEnvVar, saved);
        }
    }

    [Fact]
    public void CheckForUpdates_QuietAndVersionRuns_Skipped()
    {
        // No network, no output: version output stays parseable for the GUI probe.
        UpdateChecker.CheckForUpdates(["-v"]);
        UpdateChecker.CheckForUpdates(["-q", "game.iso"]);
        UpdateChecker.CheckForUpdates(["-Q", "game.iso"]);
        Assert.Equal(string.Empty, _errCapture.ToString());
    }

    private string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiso_update_{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }
}
