namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="ToolLocator"/>'s shared lookup chain (explicit override,
/// app-directory sibling, <c>PATH</c>) that the GUI uses to find the shipped
/// <c>XISOSharp</c> CLI. Extraction-directory behavior of single-file bundles
/// cannot be simulated from the test host (its process directory equals the app
/// base directory), so that case is covered by the published-bundle probe.
/// </summary>
[Collection("Sequential")]
public class ToolLocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _appDirFiles = [];
    private readonly string? _savedPath;

    public ToolLocatorTests()
    {
        _savedPath = Environment.GetEnvironmentVariable("PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _savedPath);
        foreach (string file in _appDirFiles)
        {
            TryDelete(file);
        }

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // ignored
            }
        }

        GC.SuppressFinalize(this);
    }

    private static string UniqueName() => $"xiso_tool_{Guid.NewGuid():N}";

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_locator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateAppDirSibling(string baseName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ToolLocator.GetFileName(baseName));
        File.WriteAllText(path, "stub");
        _appDirFiles.Add(path);
        return path;
    }

    [Fact]
    public void Resolve_ExistingOverride_IsReturnedVerbatim()
    {
        string overridePath = Path.Combine(TempDir(), ToolLocator.GetFileName(UniqueName()));
        File.WriteAllText(overridePath, "stub");

        Assert.Equal(overridePath, ToolLocator.Resolve(overridePath, "missing.exe", "missing"));
    }

    [Fact]
    public void Resolve_BlankOverride_IsIgnored()
    {
        string baseName = UniqueName();

        Assert.Equal(CreateAppDirSibling(baseName), ToolLocator.ResolveByBaseName("   ", baseName));
    }

    [Fact]
    public void Resolve_MissingOverride_FallsThroughToAppDirSibling()
    {
        string baseName = UniqueName();

        Assert.Equal(
            CreateAppDirSibling(baseName),
            ToolLocator.ResolveByBaseName(Path.Combine(TempDir(), "nope.exe"), baseName));
    }

    [Fact]
    public void ResolveByBaseName_FindsAppDirSibling()
    {
        string baseName = UniqueName();

        Assert.Equal(CreateAppDirSibling(baseName), ToolLocator.ResolveByBaseName(null, baseName));
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsNull()
    {
        // A GUID name can neither exist as a sibling nor on PATH.
        Assert.Null(ToolLocator.ResolveByBaseName(null, UniqueName()));
    }

    [Fact]
    public void Resolve_NotBesideApp_FallsThroughToPath()
    {
        string baseName = UniqueName();
        string dir = TempDir();
        string onPath = Path.Combine(dir, ToolLocator.GetFileName(baseName));
        File.WriteAllText(onPath, "stub");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _savedPath);

        Assert.Equal(onPath, ToolLocator.ResolveByBaseName(null, baseName));
    }

    [Fact]
    public void FindOnPath_DirectMatch_IsReturned()
    {
        string baseName = UniqueName();
        string dir = TempDir();
        string file = Path.Combine(dir, ToolLocator.GetFileName(baseName));
        File.WriteAllText(file, "stub");
        Environment.SetEnvironmentVariable("PATH", dir);

        Assert.Equal(file, ToolLocator.FindOnPath(ToolLocator.GetFileName(baseName)));
    }

    [Fact]
    public void GetFileName_IsOsAware() =>
        Assert.Equal(
            OperatingSystem.IsWindows() ? "extract-xiso.exe" : "extract-xiso",
            ToolLocator.GetFileName("extract-xiso"));

    [Fact]
    public void GetFileName_EmptyBaseName_Throws() =>
        Assert.Throws<ArgumentException>(() => ToolLocator.GetFileName(string.Empty));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
