#if NET10_0_OR_GREATER
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="ScreenshotService"/>: file-name construction,
/// folder resolution, and collision-free path selection. The render/save path
/// itself needs a live Avalonia window and is exercised by the GUI smoke test.
/// </summary>
public class ScreenshotServiceTests
{
    /// <summary>Verifies the file name embeds the timestamp with a PNG extension.</summary>
    [Fact]
    public void BuildFileName_UsesTimestampAndPngExtension()
    {
        string name = ScreenshotService.BuildFileName(new DateTime(2026, 10, 1, 14, 30, 45, 123));

        Assert.Equal("screenshot_20261001_143045_123.png", name);
    }

    /// <summary>Verifies the file name contains no path separators or invalid characters.</summary>
    [Fact]
    public void BuildFileName_ContainsNoInvalidFileNameCharacters()
    {
        string name = ScreenshotService.BuildFileName(DateTime.Now);

        Assert.Equal(name, Path.GetFileName(name));
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    /// <summary>Verifies the primary folder sits beside the running executable.</summary>
    [Fact]
    public void PrimaryFolder_IsBesideTheExecutable()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Screenshot"), ScreenshotService.PrimaryFolder);
    }

    /// <summary>Verifies the fallback folder lives under the per-user application data root.</summary>
    [Fact]
    public void FallbackFolder_IsUnderLocalApplicationData()
    {
        string path = ScreenshotService.FallbackFolder;

        Assert.EndsWith(Path.Combine("XISOSharp", "Screenshot"), path, StringComparison.Ordinal);
    }

    /// <summary>Verifies an unused name is returned unchanged.</summary>
    [Fact]
    public void ResolveUniquePath_NoCollision_ReturnsCandidate()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xiso_shot");
        string path = ScreenshotService.ResolveUniquePath(folder, "screenshot.png", _ => false);

        Assert.Equal(Path.Combine(folder, "screenshot.png"), path);
    }

    /// <summary>Verifies taken names get a numeric suffix before the extension.</summary>
    [Fact]
    public void ResolveUniquePath_Collisions_AppendCounter()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xiso_shot");
        HashSet<string> taken = new(StringComparer.Ordinal)
        {
            Path.Combine(folder, "screenshot.png"),
            Path.Combine(folder, "screenshot_1.png"),
        };

        string path = ScreenshotService.ResolveUniquePath(folder, "screenshot.png", taken.Contains);

        Assert.Equal(Path.Combine(folder, "screenshot_2.png"), path);
    }

    /// <summary>Verifies a GUID suffix is used once every numbered candidate is taken.</summary>
    [Fact]
    public void ResolveUniquePath_Exhausted_ReturnsGuidSuffix()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xiso_shot");
        string path = ScreenshotService.ResolveUniquePath(folder, "screenshot.png", _ => true);

        Assert.StartsWith(Path.Combine(folder, "screenshot_"), path, StringComparison.Ordinal);
        Assert.Matches(@"_[0-9a-f]{32}\.png$", path);
    }
}
#endif
