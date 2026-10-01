using System.Runtime.InteropServices;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Cli.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="UpdateChecker"/>: version parsing, update
/// comparison, RID mapping, cache round-trips, and asset lookup, complementing
/// <c>XisoUpdateTests</c>.
/// </summary>
public class UpdateCheckerExtraTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>Removes temporary directories.</summary>
    public void Dispose()
    {
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Verifies version parsing across tag shapes.</summary>
    [Theory]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("v1.2.3", "1.2.3", false)]
    [InlineData("V1.2.3", "1.2.3", false)]
    [InlineData("1.2", "1.2", false)]
    [InlineData("1.2.3-alpha.0.10", "1.2.3", true)]
    [InlineData("1.2.3+build.5", "1.2.3", false)]
    [InlineData("1.2.3-alpha+build.5", "1.2.3", true)]
    public void TryParseVersion_ParsesShapes(string input, string expectedCore, bool expectedPrerelease)
    {
        Assert.True(UpdateChecker.TryParseVersion(input, out Version core, out bool prerelease));
        Assert.Equal(Version.Parse(expectedCore), core);
        Assert.Equal(expectedPrerelease, prerelease);
    }

    /// <summary>Verifies unparseable input is rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public void TryParseVersion_Invalid_ReturnsFalse(string? input)
    {
        Assert.False(UpdateChecker.TryParseVersion(input, out Version core, out bool prerelease));
        Assert.Equal(new Version(0, 0), core);
        Assert.False(prerelease);
    }

    /// <summary>Verifies update comparison across newer/older/prerelease combinations.</summary>
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("1.0.2", "1.0.1", false)]
    [InlineData("1.0.1-alpha.0.5", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.1-beta.1", false)]
    [InlineData("1.0.0", "v1.0.1", true)]
    [InlineData("garbage", "1.0.1", false)]
    [InlineData("1.0.0", null, false)]
    public void IsUpdateAvailable_Compares(string? local, string? remote, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsUpdateAvailable(local, remote));
    }

    /// <summary>Verifies RID mapping for supported platform pairs.</summary>
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("MacOsX-x64")]
    [InlineData("MacOsX-arm64")]
    public void MapRid_KnownCombinations(string expected)
    {
        string rid = expected switch
        {
            "win-x64" => UpdateChecker.MapRid(OSPlatform.Windows, Architecture.X64)!,
            "win-arm64" => UpdateChecker.MapRid(OSPlatform.Windows, Architecture.Arm64)!,
            "linux-x64" => UpdateChecker.MapRid(OSPlatform.Linux, Architecture.X64)!,
            "linux-arm64" => UpdateChecker.MapRid(OSPlatform.Linux, Architecture.Arm64)!,
            "MacOsX-x64" => UpdateChecker.MapRid(OSPlatform.OSX, Architecture.X64)!,
            _ => UpdateChecker.MapRid(OSPlatform.OSX, Architecture.Arm64)!,
        };
        Assert.Equal(expected, rid);
    }

    /// <summary>Verifies unsupported architectures and platforms map to null.</summary>
    [Fact]
    public void MapRid_Unsupported_ReturnsNull()
    {
        Assert.Null(UpdateChecker.MapRid(OSPlatform.Windows, Architecture.X86));
        Assert.Null(UpdateChecker.MapRid(OSPlatform.Linux, Architecture.Arm));
        Assert.Null(UpdateChecker.MapRid(OSPlatform.Create("FREEBSD"), Architecture.X64));
    }

    /// <summary>Verifies the current platform maps consistently.</summary>
    [Fact]
    public void MapCurrentRid_MatchesPlatformMapping()
    {
        string? expected =
            OperatingSystem.IsWindows() ? UpdateChecker.MapRid(OSPlatform.Windows, RuntimeInformation.OSArchitecture) :
            OperatingSystem.IsLinux() ? UpdateChecker.MapRid(OSPlatform.Linux, RuntimeInformation.OSArchitecture) :
            OperatingSystem.IsMacOS() ? UpdateChecker.MapRid(OSPlatform.OSX, RuntimeInformation.OSArchitecture) :
            null;
        Assert.Equal(expected, UpdateChecker.MapCurrentRid());
    }

    /// <summary>Verifies asset-name construction strips the tag prefix.</summary>
    [Theory]
    [InlineData("1.2.3", "win-x64", "release_1.2.3_win-x64.zip")]
    [InlineData("v1.2.3", "linux-arm64", "release_1.2.3_linux-arm64.zip")]
    [InlineData("V1.2.3", "MacOsX-x64", "release_1.2.3_MacOsX-x64.zip")]
    public void BuildAssetName_Formats(string tag, string rid, string expected)
    {
        Assert.Equal(expected, UpdateChecker.BuildAssetName(tag, rid));
    }

    /// <summary>Verifies the default cache path lives under the app folder.</summary>
    [Fact]
    public void DefaultCachePath_EndsWithAppCacheFile()
    {
        string path = UpdateChecker.DefaultCachePath();
        Assert.EndsWith(Path.Combine("XISOSharp", "update-check.json"), path, StringComparison.Ordinal);
    }

    /// <summary>Verifies a missing cache file reads as null.</summary>
    [Fact]
    public void ReadCache_MissingFile_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ReadCache(Path.Combine(CreateTempDir(), "nope.json")));
    }

    /// <summary>Verifies malformed cache content reads as null.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"tag\":\"1.0.0\"}")]
    [InlineData("{\"checkedUtc\":\"not-a-date\"}")]
    public void ReadCache_InvalidContent_ReturnsNull(string json)
    {
        string path = Path.Combine(CreateTempDir(), "cache.json");
        File.WriteAllText(path, json);
        Assert.Null(UpdateChecker.ReadCache(path));
    }

    /// <summary>Verifies a written cache round-trips every field.</summary>
    [Fact]
    public void WriteCache_ReadCache_RoundTrips()
    {
        string path = Path.Combine(CreateTempDir(), "cache.json");
        ReleaseInfo info = new(
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            "1.2.3",
            "https://example.com/release",
            "release_1.2.3_win-x64.zip",
            "https://example.com/asset.zip");
        UpdateChecker.WriteCache(path, info);

        ReleaseInfo? read = UpdateChecker.ReadCache(path);

        Assert.NotNull(read);
        Assert.Equal(info.CheckedUtc, read.CheckedUtc);
        Assert.Equal(info.Tag, read.Tag);
        Assert.Equal(info.Url, read.Url);
        Assert.Equal(info.AssetName, read.AssetName);
        Assert.Equal(info.AssetUrl, read.AssetUrl);
    }

    /// <summary>Verifies quotes and backslashes survive the hand-rolled JSON writer.</summary>
    [Fact]
    public void WriteCache_EscapesSpecialCharacters()
    {
        string path = Path.Combine(CreateTempDir(), "cache.json");
        ReleaseInfo info = new(DateTime.UtcNow, "1.0.0", "https://example.com/a\"b\\c", null, null);
        UpdateChecker.WriteCache(path, info);

        ReleaseInfo? read = UpdateChecker.ReadCache(path);

        Assert.NotNull(read);
        Assert.Equal("https://example.com/a\"b\\c", read.Url);
        Assert.Null(read.AssetName);
        Assert.Null(read.AssetUrl);
    }

    /// <summary>Verifies asset lookup selects the matching RID asset.</summary>
    [Fact]
    public void FindAssetUrl_MatchingAsset_ReturnsUrl()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {
              "assets": [
                { "name": "release_1.2.3_linux-x64.zip", "browser_download_url": "https://example.com/linux.zip" },
                { "name": "release_1.2.3_win-x64.zip", "browser_download_url": "https://example.com/win.zip" }
              ]
            }
            """);

        Assert.Equal("https://example.com/win.zip",
            UpdateChecker.FindAssetUrl(doc.RootElement, "1.2.3", "win-x64"));
    }

    /// <summary>Verifies asset lookup returns null when nothing matches.</summary>
    [Theory]
    [InlineData("{\"assets\":[]}")]
    [InlineData("{}")]
    [InlineData("{\"assets\":\"nope\"}")]
    public void FindAssetUrl_NoMatch_ReturnsNull(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Null(UpdateChecker.FindAssetUrl(doc.RootElement, "1.2.3", "win-x64"));
    }
}
