namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoPaths"/> (TODO #15, xdvdfs #36).
/// </summary>
[Collection("Sequential")]
public sealed class XisoPathsTests : IDisposable
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_paths_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void AreSamePath_IdenticalStrings_Match()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "game.iso");
        File.WriteAllText(file, "x");
        Assert.True(XisoPaths.AreSamePath(file, file));
    }

    [Fact]
    public void AreSamePath_RelativeVsAbsolute_Match()
    {
        string dir = CreateTempDir();
        string file = Path.Combine(dir, "game.iso");
        File.WriteAllText(file, "x");
        string cwd = Directory.GetCurrentDirectory();
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
        string dir = CreateTempDir();
        Assert.True(XisoPaths.AreSamePath(dir, dir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AreSamePath_CaseRule_FollowsOsConvention()
    {
        string dir = CreateTempDir();
        string lower = Path.Combine(dir, "game.iso");
        string upper = Path.Combine(dir, "GAME.ISO");
        bool expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, XisoPaths.AreSamePath(lower, upper));
    }

    [Fact]
    public void AreSamePath_DifferentFiles_Differ()
    {
        string dir = CreateTempDir();
        string a = Path.Combine(dir, "a.iso");
        string b = Path.Combine(dir, "b.iso");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        Assert.False(XisoPaths.AreSamePath(a, b));
    }

    [Fact]
    public void AreSamePath_FileVsDirectory_Differ()
    {
        string dir = CreateTempDir();
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string file = Path.Combine(dir, "sub.iso");
        File.WriteAllText(file, "x");
        Assert.False(XisoPaths.AreSamePath(sub, file));
    }

    [Theory]
    [InlineData(null, "a.iso")]
    [InlineData("a.iso", null)]
    [InlineData("", "a.iso")]
    [InlineData("   ", "a.iso")]
    public void AreSamePath_MissingSide_ReturnsFalse(string? a, string? b) => Assert.False(XisoPaths.AreSamePath(a, b));

    [Fact]
    public void IsWithinDirectory_DirectChild_Matches()
    {
        string dir = CreateTempDir();
        Assert.True(XisoPaths.IsWithinDirectory(Path.Combine(dir, "out.iso"), dir));
        Assert.True(XisoPaths.IsWithinDirectory(Path.Combine(dir, "sub", "out.iso"), dir));
    }

    [Fact]
    public void IsWithinDirectory_SiblingPrefix_DoesNotMatch()
    {
        string dir = CreateTempDir();
        string sibling = dir + "2";
        Directory.CreateDirectory(sibling);
        _tempDirs.Add(sibling);
        Assert.False(XisoPaths.IsWithinDirectory(Path.Combine(sibling, "out.iso"), dir));
    }

    [Fact]
    public void IsWithinDirectory_SameDir_DoesNotMatch()
    {
        string dir = CreateTempDir();
        Assert.False(XisoPaths.IsWithinDirectory(dir, dir));
    }

    [Fact]
    public void IsWithinDirectory_Parent_DoesNotMatch()
    {
        string dir = CreateTempDir();
        Assert.False(XisoPaths.IsWithinDirectory(Path.GetDirectoryName(dir), dir));
    }

    [Fact]
    public void TrimTrailingSeparators_RootSlash_Survives() => Assert.Equal("/", XisoPaths.TrimTrailingSeparators("/"));

    [Fact]
    public void TrimTrailingSeparators_Empty_StaysEmpty() => Assert.Equal("", XisoPaths.TrimTrailingSeparators(""));

    [Theory]
    [InlineData("out/", "out")]
    [InlineData("out//", "out")]
    public void TrimTrailingSeparators_Relative_Strips(string input, string expected) =>
        Assert.Equal(expected, XisoPaths.TrimTrailingSeparators(input));

    [WindowsOnlyFact]
    public void TrimTrailingSeparators_DriveRoot_Survives() =>
        Assert.Equal(@"C:\", XisoPaths.TrimTrailingSeparators(@"C:\"));

    [WindowsOnlyTheory]
    [InlineData(@"C:\out\", @"C:\out")]
    [InlineData(@"C:\out\\", @"C:\out")]
    [InlineData(@"C:\out\/", @"C:\out")]
    public void TrimTrailingSeparators_DriveSubdir_Strips(string input, string expected) =>
        Assert.Equal(expected, XisoPaths.TrimTrailingSeparators(input));

    [WindowsOnlyFact]
    public void TrimTrailingSeparators_UncRoot_Survives() =>
        Assert.Equal(@"\\server\share\", XisoPaths.TrimTrailingSeparators(@"\\server\share\"));

    [WindowsOnlyFact]
    public void TrimTrailingSeparators_UncSubdir_Strips() =>
        Assert.Equal(@"\\server\share\dir",
            XisoPaths.TrimTrailingSeparators(@"\\server\share\dir\"));

    [WindowsOnlyFact]
    public void AreSamePath_Unc_TrailingSeparator_Match() =>
        Assert.True(XisoPaths.AreSamePath(@"\\server\share\dir", @"\\server\share\dir\"));

    [WindowsOnlyFact]
    public void IsWithinDirectory_Unc_TrailingSeparator_Matches() =>
        Assert.True(XisoPaths.IsWithinDirectory(
            @"\\server\share\dir\out.iso", @"\\server\share\dir\"));
}
