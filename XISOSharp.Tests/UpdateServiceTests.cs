#if NET10_0_OR_GREATER
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateService"/>: version parsing, update
/// comparison, and release-page URL fallback. The network probe itself is
/// best-effort and covered by the CLI-side <c>UpdateChecker</c> tests with the
/// same comparison contract.
/// </summary>
public class UpdateServiceTests
{
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
        Assert.True(UpdateService.TryParseVersion(input, out Version core, out bool prerelease));
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
        Assert.False(UpdateService.TryParseVersion(input, out Version core, out bool prerelease));
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
        Assert.Equal(expected, UpdateService.IsUpdateAvailable(local, remote));
    }

    /// <summary>Verifies the release URL falls back to the releases index.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReleasePageUrl_Blank_FallsBackToReleasesPage(string? url)
    {
        Assert.Equal(UpdateService.ReleasesPageUrl, UpdateService.ReleasePageUrl(url));
    }

    /// <summary>Verifies a provided release URL is used verbatim.</summary>
    [Fact]
    public void ReleasePageUrl_Provided_ReturnsUrl()
    {
        Assert.Equal("https://github.com/purelogiccode/XISOSharp/releases/tag/1.0.2",
            UpdateService.ReleasePageUrl("https://github.com/purelogiccode/XISOSharp/releases/tag/1.0.2"));
    }
}
#endif
