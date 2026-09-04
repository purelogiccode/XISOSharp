namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoPaths"/> (TODO #15, xdvdfs #36).
/// </summary>
public sealed class XisoPathsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_paths_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void AreSamePath_IdenticalStrings_Match()
    {
        var dir = CreateTempDir();
        var file = Path.Combine(dir, "game.iso");
        File.WriteAllText(file, "x");
        Assert.True(XisoPaths.AreSamePath(file, file));
    }

    [Fact]
    public void AreSamePath_RelativeVsAbsolute_Match()
    {
        var dir = CreateTempDir();
        var file = Path.Combine(dir, "game.iso");
        File.WriteAllText(file, "x");
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            Assert.True(XisoPaths.AreSamePath("game.iso", file));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void AreSamePath_TrailingSeparator_Match()
    {
        var dir = CreateTempDir();
        Assert.True(XisoPaths.AreSamePath(dir, dir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AreSamePath_CaseRule_FollowsOsConvention()
    {
        var dir = CreateTempDir();
        var lower = Path.Combine(dir, "game.iso");
        var upper = Path.Combine(dir, "GAME.ISO");
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, XisoPaths.AreSamePath(lower, upper));
    }

    [Fact]
    public void AreSamePath_DifferentFiles_Differ()
    {
        var dir = CreateTempDir();
        var a = Path.Combine(dir, "a.iso");
        var b = Path.Combine(dir, "b.iso");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        Assert.False(XisoPaths.AreSamePath(a, b));
    }

    [Fact]
    public void AreSamePath_FileVsDirectory_Differ()
    {
        var dir = CreateTempDir();
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(dir, "sub.iso");
        File.WriteAllText(file, "x");
        Assert.False(XisoPaths.AreSamePath(sub, file));
    }

    [Theory]
    [InlineData(null, "a.iso")]
    [InlineData("a.iso", null)]
    [InlineData("", "a.iso")]
    [InlineData("   ", "a.iso")]
    public void AreSamePath_MissingSide_ReturnsFalse(string? a, string? b)
    {
        Assert.False(XisoPaths.AreSamePath(a, b));
    }

    [Fact]
    public void IsWithinDirectory_DirectChild_Matches()
    {
        var dir = CreateTempDir();
        Assert.True(XisoPaths.IsWithinDirectory(Path.Combine(dir, "out.iso"), dir));
        Assert.True(XisoPaths.IsWithinDirectory(Path.Combine(dir, "sub", "out.iso"), dir));
    }

    [Fact]
    public void IsWithinDirectory_SiblingPrefix_DoesNotMatch()
    {
        var dir = CreateTempDir();
        var sibling = dir + "2";
        Directory.CreateDirectory(sibling);
        _tempDirs.Add(sibling);
        Assert.False(XisoPaths.IsWithinDirectory(Path.Combine(sibling, "out.iso"), dir));
    }

    [Fact]
    public void IsWithinDirectory_SameDir_DoesNotMatch()
    {
        var dir = CreateTempDir();
        Assert.False(XisoPaths.IsWithinDirectory(dir, dir));
    }

    [Fact]
    public void IsWithinDirectory_Parent_DoesNotMatch()
    {
        var dir = CreateTempDir();
        Assert.False(XisoPaths.IsWithinDirectory(Path.GetDirectoryName(dir), dir));
    }

    [Fact]
    public void TrimTrailingSeparators_RootSlash_Survives()
    {
        Assert.Equal("/", XisoPaths.TrimTrailingSeparators("/"));
    }

    [Fact]
    public void TrimTrailingSeparators_Empty_StaysEmpty()
    {
        Assert.Equal("", XisoPaths.TrimTrailingSeparators(""));
    }

    [Theory]
    [InlineData("out/", "out")]
    [InlineData("out//", "out")]
    public void TrimTrailingSeparators_Relative_Strips(string input, string expected)
    {
        Assert.Equal(expected, XisoPaths.TrimTrailingSeparators(input));
    }

    [Fact]
    public void TrimTrailingSeparators_DriveRoot_Survives()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(@"C:\", XisoPaths.TrimTrailingSeparators(@"C:\"));
    }

    [Theory]
    [InlineData(@"C:\out\", @"C:\out")]
    [InlineData(@"C:\out\\", @"C:\out")]
    [InlineData(@"C:\out\/", @"C:\out")]
    public void TrimTrailingSeparators_DriveSubdir_Strips(string input, string expected)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(expected, XisoPaths.TrimTrailingSeparators(input));
    }

    [Fact]
    public void TrimTrailingSeparators_UncRoot_Survives()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(@"\\server\share\", XisoPaths.TrimTrailingSeparators(@"\\server\share\"));
    }

    [Fact]
    public void TrimTrailingSeparators_UncSubdir_Strips()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(@"\\server\share\dir",
            XisoPaths.TrimTrailingSeparators(@"\\server\share\dir\"));
    }

    [Fact]
    public void AreSamePath_Unc_TrailingSeparator_Match()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(XisoPaths.AreSamePath(@"\\server\share\dir", @"\\server\share\dir\"));
    }

    [Fact]
    public void IsWithinDirectory_Unc_TrailingSeparator_Matches()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(XisoPaths.IsWithinDirectory(
            @"\\server\share\dir\out.iso", @"\\server\share\dir\"));
    }
}
