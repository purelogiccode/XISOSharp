namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="WaxGlob"/> wax-compatible glob matcher.
/// Mirrors semantics of Rust wax crate 0.6 as used by remap filesystem.
/// </summary>
public class WaxGlobTests
{
    // -----------------------------------------------------------------
    // Basic IsMatch
    // -----------------------------------------------------------------

    [Fact]
    public void Constructor_PatternProperty_ReturnsOriginal()
    {
        var g = new WaxGlob("src/*.txt");
        Assert.Equal("src/*.txt", g.Pattern);
    }

    [Theory]
    [InlineData("*.txt", "file.txt", true)]
    [InlineData("*.txt", "file.TXT", true)] // case-insensitive
    [InlineData("*.txt", "dir/file.txt", false)]
    [InlineData("*", "file.txt", true)]
    [InlineData("*", "a/b", false)]
    public void Star_MatchesWithinSingleSegment(string pattern, string candidate, bool expected)
    {
        var g = new WaxGlob(pattern);
        Assert.Equal(expected, g.IsMatch(candidate));
    }

    [Theory]
    [InlineData("**", "anything", true)]
    [InlineData("**", "a/b/c", true)]
    [InlineData("**", "", true)] // "**" with single segment count==1 => "(.*)" matches empty too
    [InlineData("src/**", "src", true)]
    [InlineData("src/**", "src/file.txt", true)]
    [InlineData("src/**", "src/a/b/c", true)]
    [InlineData("src/**", "other/file", false)]
    [InlineData("**/file.txt", "file.txt", true)]
    [InlineData("**/file.txt", "a/file.txt", true)]
    [InlineData("**/file.txt", "a/b/file.txt", true)]
    public void DoubleStar_MatchesAcrossSegments(string pattern, string candidate, bool expected)
    {
        var g = new WaxGlob(pattern);
        Assert.Equal(expected, g.IsMatch(candidate));
    }

    [Theory]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/b/c", false)]
    public void DoubleStar_InMiddle_MatchesZeroOrMoreSegments(string pattern, string candidate, bool expected)
    {
        var g = new WaxGlob(pattern);
        Assert.Equal(expected, g.IsMatch(candidate));
    }

    [Fact]
    public void IsMatch_CaseInsensitive()
    {
        var g = new WaxGlob("SRC/*.TXT");
        Assert.True(g.IsMatch("src/file.txt"));
        Assert.True(g.IsMatch("SRC/FILE.TXT"));
        Assert.True(g.IsMatch("Src/File.Txt"));
    }

    [Fact]
    public void LeadingSlash_IsTrimmed()
    {
        var g = new WaxGlob("/src/*.txt");
        Assert.True(g.IsMatch("src/file.txt"));
        // Wax patterns are relative; leading slash in pattern is ignored.
        // Candidate matching is against relative paths without leading slash.
        Assert.False(g.IsMatch("/src/file.txt"));
    }

    [Theory]
    [InlineData("./src/*.txt", "src/file.txt", true)]
    [InlineData("././src/*.txt", "src/file.txt", true)]
    [InlineData(".", "", true)]
    [InlineData("./", "", true)]
    public void DotSlashPrefix_IsNormalized(string pattern, string candidate, bool expected)
    {
        var g = new WaxGlob(pattern);
        Assert.Equal(expected, g.IsMatch(candidate));
    }

    [Fact]
    public void EmptyPattern_MatchesOnlyEmpty()
    {
        var g = new WaxGlob("");
        Assert.True(g.IsMatch(""));
        Assert.False(g.IsMatch("a"));
        Assert.False(g.IsMatch("a/b"));
    }

    // -----------------------------------------------------------------
    // Captures
    // -----------------------------------------------------------------

    [Fact]
    public void GetCaptures_SingleStar_ReturnsSegment()
    {
        var g = new WaxGlob("src/*.txt");
        var caps = g.GetCaptures("src/file.txt");
        Assert.NotNull(caps);
        // caps[0] whole match, caps[1] star capture
        Assert.Equal("src/file.txt", caps[0]);
        Assert.Equal("file", caps[1]);
    }

    [Fact]
    public void GetCaptures_Star_WholeMatchIsIndexZero()
    {
        var g = new WaxGlob("*");
        var caps = g.GetCaptures("hello");
        Assert.NotNull(caps);
        Assert.Equal("hello", caps[0]);
        Assert.Equal("hello", caps[1]);
        Assert.Equal("hello", g.GetCapture("hello", 0));
        Assert.Equal("hello", g.GetCapture("hello", 1));
    }

    [Fact]
    public void GetCaptures_TrailingDoubleStar_CapturesRemainder()
    {
        var g = new WaxGlob("src/**");
        var caps = g.GetCaptures("src/a/b/c.txt");
        Assert.NotNull(caps);
        Assert.Equal("src/a/b/c.txt", caps[0]);
        // group 1 is remainder after src/
        Assert.Equal("a/b/c.txt", caps[1]);

        var caps2 = g.GetCaptures("src");
        Assert.NotNull(caps2);
        // trailing ** optional group may be empty or null -> stored as ""
        Assert.Equal("src", caps2[0]);
        // caps[1] should be empty when no remainder
        Assert.Equal(string.Empty, caps2[1]);
    }

    [Fact]
    public void GetCaptures_LeadingDoubleStar_CapturesPrefix()
    {
        var g = new WaxGlob("**/file.txt");
        var caps = g.GetCaptures("a/b/file.txt");
        Assert.NotNull(caps);
        Assert.Equal("a/b/file.txt", caps[0]);
        // For "**/file.txt" the leading ** captures "a/b/" inclusive?
        // Regex "^((?:[^/]+/)*)file\\.txt$" -> group 1 = "a/b/"
        Assert.Equal("a/b/", caps[1]);
    }

    [Fact]
    public void GetCaptures_MiddleDoubleStar_CapturesMiddleSegment()
    {
        var g = new WaxGlob("a/**/b");
        var caps = g.GetCaptures("a/x/y/b");
        Assert.NotNull(caps);
        Assert.Equal("a/x/y/b", caps[0]);
        Assert.Equal("x/y/", caps[1]);

        var caps2 = g.GetCaptures("a/b");
        Assert.NotNull(caps2);
        Assert.Equal("a/b", caps2[0]);
        Assert.Equal(string.Empty, caps2[1]);
    }

    [Fact]
    public void GetCaptures_BraceAlternatives_CapturesChoice()
    {
        // "{a,b}" should match either and capture the choice as group 1
        var g = new WaxGlob("src/{a,b}/file.txt");
        Assert.True(g.IsMatch("src/a/file.txt"));
        Assert.True(g.IsMatch("src/b/file.txt"));
        Assert.False(g.IsMatch("src/c/file.txt"));

        var caps = g.GetCaptures("src/a/file.txt");
        Assert.NotNull(caps);
        // Whole match + one capture for the brace alternatives
        Assert.Equal("src/a/file.txt", caps[0]);
        Assert.Equal("a", caps[1]);

        var caps2 = g.GetCaptures("src/b/file.txt");
        Assert.NotNull(caps2);
        Assert.Equal("b", caps2[1]);
    }

    [Fact]
    public void GetCaptures_QuestionMark_CapturesSingleChar()
    {
        var g = new WaxGlob("a?c");
        var caps = g.GetCaptures("abc");
        Assert.NotNull(caps);
        Assert.Equal("abc", caps[0]);
        Assert.Equal("b", caps[1]);
        Assert.False(g.IsMatch("ac"));
        Assert.False(g.IsMatch("abdc"));
    }

    [Fact]
    public void GetCaptures_CharClass_Matches()
    {
        var g = new WaxGlob("[abc].txt");
        Assert.True(g.IsMatch("a.txt"));
        Assert.True(g.IsMatch("b.txt"));
        Assert.False(g.IsMatch("d.txt"));
        // Char class is not capturing, only whole match
        var caps = g.GetCaptures("a.txt");
        Assert.NotNull(caps);
        Assert.Single(caps); // only whole match, no captures
        Assert.Equal("a.txt", caps[0]);
    }

    [Fact]
    public void GetCaptures_NonMatching_ReturnsNull()
    {
        var g = new WaxGlob("src/*.txt");
        var caps = g.GetCaptures("other/file.txt");
        Assert.Null(caps);
    }

    [Fact]
    public void GetCapture_OutOfRange_ReturnsEmpty()
    {
        var g = new WaxGlob("src/*.txt");
        // valid captures have size 2 (whole + star)
        Assert.Equal(string.Empty, g.GetCapture("src/file.txt", 99));
        Assert.Equal(string.Empty, g.GetCapture("src/file.txt", -1));
        // non-matching candidate returns empty for any index
        Assert.Equal(string.Empty, g.GetCapture("no/match", 0));
        Assert.Equal(string.Empty, g.GetCapture("no/match", 1));
    }

    [Fact]
    public void GetCapture_IndexZero_IsWholeMatch()
    {
        var g = new WaxGlob("a/**/b");
        Assert.Equal("a/x/b", g.GetCapture("a/x/b", 0));
        Assert.Equal("x/", g.GetCapture("a/x/b", 1));
    }

    [Fact]
    public void CaptureIndexSequence_MultipleWildcards()
    {
        // Pattern with multiple capturing wildcards should have sequential indices
        var g = new WaxGlob("*/*/*.txt");
        var caps = g.GetCaptures("a/b/c.txt");
        Assert.NotNull(caps);
        // 0 whole, 1 first *, 2 second *, 3 third * (without .txt)
        Assert.Equal(4, caps.Count);
        Assert.Equal("a/b/c.txt", caps[0]);
        Assert.Equal("a", caps[1]);
        Assert.Equal("b", caps[2]);
        Assert.Equal("c", caps[3]);
    }

    // -----------------------------------------------------------------
    // Error cases
    // -----------------------------------------------------------------

    [Fact]
    public void Constructor_EmbeddedDoubleStar_Throws()
    {
        // tree wildcard '**' must be alone as a path component
        Assert.Throws<ArgumentException>(() => new WaxGlob("a**/b"));
        Assert.Throws<ArgumentException>(() => new WaxGlob("a/b**"));
        Assert.Throws<ArgumentException>(() => new WaxGlob("a/**b"));
    }

    [Fact]
    public void Constructor_UnclosedBrace_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WaxGlob("src/{a,b"));
        Assert.Throws<ArgumentException>(() => new WaxGlob("src/{a"));
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WaxGlob(null!));
    }

    [Fact]
    public void RegexPattern_Exposed()
    {
        var g = new WaxGlob("src/*.txt");
        Assert.False(string.IsNullOrEmpty(g.RegexPattern));
        Assert.StartsWith("^", g.RegexPattern);
        Assert.EndsWith("$", g.RegexPattern);
    }

    [Theory]
    [InlineData("src/*", "src/file.txt", true)]
    [InlineData("src/*", "src/sub/file.txt", false)]
    [InlineData("src/**", "src/sub/file.txt", true)]
    [InlineData("**/*.tmp", "a.tmp", true)]
    [InlineData("**/*.tmp", "x/y/a.tmp", true)]
    public void MixedPatterns_MatchExpected(string pattern, string candidate, bool expected)
    {
        var g = new WaxGlob(pattern);
        Assert.Equal(expected, g.IsMatch(candidate));
    }

    [Fact]
    public void GetCaptures_DollarLazyStar_TreatedAsStar()
    {
        var g = new WaxGlob("file$.txt");
        // $ is wax lazy star – treated same as *
        Assert.True(g.IsMatch("fileabc.txt"));
        var caps = g.GetCaptures("fileabc.txt");
        Assert.NotNull(caps);
        Assert.Equal("abc", caps[1]);
    }

    [Fact]
    public void EscapedStar_MatchesLiteral()
    {
        var g = new WaxGlob("a\\*b");
        Assert.True(g.IsMatch("a*b"));
        Assert.False(g.IsMatch("axb"));
        var caps = g.GetCaptures("a*b");
        Assert.NotNull(caps);
        // escaped star should not be capturing
        Assert.Single(caps);
    }
}