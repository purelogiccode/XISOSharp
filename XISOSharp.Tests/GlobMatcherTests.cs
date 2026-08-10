namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="GlobMatcher"/> glob pattern matching semantics.
/// </summary>
public class GlobMatcherTests
{
    private static bool Matches(string pattern, string path)
    {
        return new GlobMatcher([pattern]).IsMatch(path);
    }

    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.TXT", true)] // case-insensitive
    [InlineData("*.txt", "dir/file.txt", false)] // * does not cross segments
    [InlineData("*.txt", "file.md", false)]
    [InlineData("*", "file.txt", true)]
    [InlineData("*", "dir/file.txt", false)]
    public void Star_MatchesWithinSingleSegment(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("a?c", "abdc", false)]
    public void QuestionMark_MatchesSingleCharacter(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("**/node_modules/**", "node_modules", true)]
    [InlineData("**/node_modules/**", "node_modules/x", true)]
    [InlineData("**/node_modules/**", "node_modules/a/b/c.js", true)]
    [InlineData("**/node_modules/**", "a/node_modules", true)]
    [InlineData("**/node_modules/**", "a/b/node_modules/x", true)]
    [InlineData("**/node_modules/**", "node_modules2", false)]
    [InlineData("**/node_modules/**", "a/node_modules_extra", false)]
    [InlineData("**/node_modules/**", "a/b/c.txt", false)]
    public void DoubleStar_MatchesAnyDepth(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("build/**", "build", true)] // trailing /** matches the directory itself
    [InlineData("build/**", "build/x", true)]
    [InlineData("build/**", "build/a/b/c", true)]
    [InlineData("build/**", "buildx", false)]
    [InlineData("build/**", "a/build", false)] // anchored to root
    [InlineData("build/", "build", true)] // trailing slash == /**
    [InlineData("build/", "build/x", true)]
    public void TrailingDoubleStar_MatchesDirectoryAndContents(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("**/*.tmp", "a.tmp", true)]
    [InlineData("**/*.tmp", "x/a.tmp", true)]
    [InlineData("**/*.tmp", "x/y/a.tmp", true)]
    [InlineData("**/*.tmp", "a.tmpx", false)]
    [InlineData("**", "anything", true)]
    [InlineData("**", "a/b/c", true)]
    public void DoubleStar_PrefixOrAlone(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("$SystemUpdate/**", "$SystemUpdate", true)]
    [InlineData("$SystemUpdate/**", "$SystemUpdate/f", true)]
    [InlineData("$SystemUpdate/**", "a/$SystemUpdate", false)] // no **/ prefix: root only
    [InlineData("**/$SystemUpdate/**", "a/$SystemUpdate", true)]
    [InlineData("**/$SystemUpdate/**", "a/b/$SystemUpdate/c", true)]
    [InlineData("**/$SystemUpdate/**", "a/other/f", false)]
    public void SystemUpdatePatterns(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/x/b/c", false)]
    [InlineData("a/**/b", "b", false)]
    public void DoubleStar_InMiddle(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("[abc].txt", "a.txt", true)]
    [InlineData("[abc].txt", "c.txt", true)]
    [InlineData("[abc].txt", "d.txt", false)]
    [InlineData("[!abc].txt", "d.txt", true)]
    [InlineData("[!abc].txt", "a.txt", false)]
    [InlineData("[a-c]*", "a1", true)]
    [InlineData("[a-c]*", "c2", true)]
    [InlineData("[a-c]*", "d1", false)]
    public void CharClasses(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "axb", false)]
    [InlineData(@"a\[b\]", "a[b]", true)]
    public void Escaping(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Fact]
    public void Backslashes_InPath_AreNormalized()
    {
        Assert.True(Matches("dir/*", "dir\\file.txt"));
    }

    [Fact]
    public void MultiplePatterns_AnyMatch_ReturnsTrue()
    {
        var matcher = new GlobMatcher(["*.tmp", "**/node_modules/**"]);
        Assert.True(matcher.IsMatch("x.tmp"));
        Assert.True(matcher.IsMatch("a/node_modules/b"));
        Assert.False(matcher.IsMatch("keep.txt"));
        Assert.False(matcher.IsMatch("a/b/c.cs"));
    }

    [Fact]
    public void EmptyPatterns_MatchesNothing()
    {
        var matcher = new GlobMatcher([]);
        Assert.False(matcher.IsMatch("file.txt"));
        Assert.False(matcher.IsMatch("a/b"));
    }

    [Fact]
    public void NullOrEmptyPath_MatchesNothing()
    {
        var matcher = new GlobMatcher(["*"]);
        Assert.False(matcher.IsMatch(null));
        Assert.False(matcher.IsMatch(""));
    }

    [Fact]
    public void NullPatterns_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobMatcher(null!));
    }

    [Theory]
    [InlineData("a/**/", "a", true)]
    [InlineData("a/**/", "a/x", true)]
    [InlineData("a/**/", "a/x/y/z", true)]
    [InlineData("a/**/", "b", false)]
    [InlineData("a/**/**/b", "a/b", true)]
    [InlineData("a/**/**/b", "a/x/y/b", true)]
    [InlineData("a/b**/", "a/b", true)] // segment ending in '**' keeps directory semantics
    [InlineData("a/b**/", "a/b1/x", true)]
    [InlineData("a/b**/", "a/c", false)]
    public void TrailingSlash_AfterDoubleStar(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Matches(pattern, path));
    }

    [Theory]
    [InlineData("[\\z].txt", "z.txt", true)] // escaped char inside class is a literal
    [InlineData("[\\z].txt", "q.txt", false)]
    [InlineData("[a", "a", false)] // unclosed class: '[' treated as literal, never throws
    [InlineData("[a]", "]", false)] // lone ']' is a literal
    [InlineData("[[a]", "a", false)] // nested class not supported; no crash
    [InlineData("[z-a].txt", "[z-a].txt", true)] // descending range: treated as literal
    [InlineData("[z-a].txt", "z.txt", false)]
    [InlineData("[\\z-a].txt", "[z-a].txt", true)] // escaped endpoint + descending range
    [InlineData("[9-\\0].txt", "[9-0].txt", true)] // escaped digit endpoint: literal
    [InlineData("[0-9].txt", "5.txt", true)] // valid range still works
    [InlineData("[a-].txt", "a.txt", true)] // trailing dash is a literal class member
    [InlineData("[a-].txt", "-.txt", true)]
    [InlineData("[-a].txt", "-a.txt", false)] // class matches exactly one character
    [InlineData("[-a].txt", "-.txt", true)] // leading dash is a literal class member
    public void MalformedPatterns_NeverThrow(string pattern, string path, bool expected)
    {
        var matcher = new GlobMatcher([pattern]);
        Assert.Equal(expected, matcher.IsMatch(path));
    }
}
