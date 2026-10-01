namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="GlobMatcher"/> capture groups, <c>TryMatch</c>,
/// and pattern edge cases beyond <see cref="GlobMatcherTests"/>.
/// </summary>
public class GlobMatcherExtraTests
{
    /// <summary>Verifies wildcard captures are returned for a matching pattern.</summary>
    [Fact]
    public void MatchWithGroups_Star_ReturnsCaptures()
    {
        GlobMatcher matcher = new(["src/*.txt"]);
        GlobMatcher.GlobMatchResult result = matcher.MatchWithGroups("src/a.txt");
        Assert.True(result.IsMatch);
        Assert.Equal(2, result.Groups.Length);
        Assert.Equal("src/a.txt", result.Groups[0]);
        Assert.Equal("a", result.Groups[1]);
    }

    /// <summary>Verifies a non-matching path returns no captures.</summary>
    [Fact]
    public void MatchWithGroups_NoMatch_ReturnsEmpty()
    {
        GlobMatcher matcher = new(["src/*.txt"]);
        GlobMatcher.GlobMatchResult result = matcher.MatchWithGroups("other/a.txt");
        Assert.False(result.IsMatch);
        Assert.Empty(result.Groups);
    }

    /// <summary>Verifies null and empty paths never match.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MatchWithGroups_NullOrEmpty_NoMatch(string? path)
    {
        GlobMatcher matcher = new(["*"]);
        GlobMatcher.GlobMatchResult result = matcher.MatchWithGroups(path);
        Assert.False(result.IsMatch);
        Assert.Empty(result.Groups);
    }

    /// <summary>Verifies backslash paths are normalized before matching.</summary>
    [Fact]
    public void MatchWithGroups_BackslashPath_Normalized()
    {
        GlobMatcher matcher = new(["src/*.txt"]);
        Assert.True(matcher.MatchWithGroups(@"src\a.txt").IsMatch);
    }

    /// <summary>Verifies the regex fallback returns the whole path when wax cannot capture.</summary>
    [Fact]
    public void MatchWithGroups_WaxIncompatiblePattern_FallsBackToWholePath()
    {
        GlobMatcher matcher = new(["!keep.txt"]);
        GlobMatcher.GlobMatchResult result = matcher.MatchWithGroups("!keep.txt");
        Assert.True(result.IsMatch);
        Assert.Equal(["!keep.txt"], result.Groups);
    }

    /// <summary>Verifies <c>TryMatch</c> populates groups on success.</summary>
    [Fact]
    public void TryMatch_Match_ReturnsTrueWithGroups()
    {
        GlobMatcher matcher = new(["data/*.bin"]);
        Assert.True(matcher.TryMatch("data/x.bin", out string[] groups));
        Assert.NotEmpty(groups);
    }

    /// <summary>Verifies <c>TryMatch</c> returns false with empty groups on mismatch.</summary>
    [Fact]
    public void TryMatch_NoMatch_ReturnsFalseWithEmptyGroups()
    {
        GlobMatcher matcher = new(["data/*.bin"]);
        Assert.False(matcher.TryMatch("data/x.txt", out string[] groups));
        Assert.Empty(groups);
    }

    /// <summary>Verifies a trailing slash matches the directory itself and its contents.</summary>
    [Theory]
    [InlineData("build", true)]
    [InlineData("build/x.txt", true)]
    [InlineData("buildx", false)]
    public void IsMatch_TrailingSlash_MatchesDirectoryAndContents(string path, bool expected)
    {
        GlobMatcher matcher = new(["build/"]);
        Assert.Equal(expected, matcher.IsMatch(path));
    }

    /// <summary>Verifies a leading double-star matches zero or more leading segments.</summary>
    [Theory]
    [InlineData("c.bin", true)]
    [InlineData("a/b/c.bin", true)]
    [InlineData("c.txt", false)]
    public void IsMatch_LeadingDoubleStar(string path, bool expected)
    {
        GlobMatcher matcher = new(["**/*.bin"]);
        Assert.Equal(expected, matcher.IsMatch(path));
    }

    /// <summary>Verifies escaped wildcards match literally.</summary>
    [Fact]
    public void IsMatch_EscapedStar_IsLiteral()
    {
        GlobMatcher matcher = new([@"a\*b.txt"]);
        Assert.True(matcher.IsMatch("a*b.txt"));
        Assert.False(matcher.IsMatch("axb.txt"));
    }

    /// <summary>Verifies a negated character class excludes the listed characters.</summary>
    [Fact]
    public void IsMatch_NegatedCharClass()
    {
        GlobMatcher matcher = new(["[!a]bc.txt"]);
        Assert.True(matcher.IsMatch("xbc.txt"));
        Assert.False(matcher.IsMatch("abc.txt"));
    }

    /// <summary>Verifies an unclosed character class degrades to a literal bracket.</summary>
    [Fact]
    public void IsMatch_UnclosedCharClass_IsLiteral()
    {
        GlobMatcher matcher = new(["a[bc.txt"]);
        Assert.True(matcher.IsMatch("a[bc.txt"));
        Assert.False(matcher.IsMatch("abc.txt"));
    }

    /// <summary>Verifies a descending range degrades to a literal bracket instead of throwing.</summary>
    [Fact]
    public void IsMatch_InvalidRange_IsLiteral()
    {
        GlobMatcher matcher = new(["a[z-a].txt"]);
        Assert.True(matcher.IsMatch("a[z-a].txt"));
    }

    /// <summary>Verifies null patterns are rejected.</summary>
    [Fact]
    public void Ctor_NullPatterns_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobMatcher(null!));
    }

    /// <summary>Verifies empty patterns are ignored while a whitespace pattern stays literal.</summary>
    [Fact]
    public void Ctor_EmptyPatterns_Ignored()
    {
        GlobMatcher matcher = new(["", null!, " "]);
        Assert.True(matcher.IsMatch(" "));
        Assert.False(matcher.IsMatch("anything"));
    }

    /// <summary>Verifies matching is case-insensitive.</summary>
    [Fact]
    public void IsMatch_CaseInsensitive()
    {
        GlobMatcher matcher = new(["SRC/*.TXT"]);
        Assert.True(matcher.IsMatch("src/a.txt"));
    }
}
