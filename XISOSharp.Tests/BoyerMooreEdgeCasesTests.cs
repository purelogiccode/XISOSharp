namespace XISOSharp.Tests;

/// <summary>
/// Edge-case and boundary tests for the BoyerMoore byte-pattern search algorithm.
/// </summary>
public class BoyerMooreEdgeCasesTests
{
    /// <summary>
    /// Verifies that a zero-length pattern returns 0 when searching any non-empty text.
    /// </summary>
    [Fact]
    public void Constructor_ZeroLengthPattern_ReturnsZeroOnEmptySearch()
    {
        var bm = new BoyerMoore([]);
        bm.Init();

        var result = bm.Search("ABC"u8.ToArray());
        Assert.Equal(0, result);
    }

    /// <summary>
    /// Verifies that Init followed by Done does not throw for a zero-length pattern.
    /// </summary>
    [Fact]
    public void Constructor_ZeroLengthPattern_InitThenDone_DoesNotThrow()
    {
        var bm = new BoyerMoore([]);
        bm.Init();
        bm.Done();
    }

    /// <summary>
    /// Verifies that calling Search before Init throws a NullReferenceException.
    /// </summary>
    [Fact]
    public void Search_BeforeInit_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        Assert.Throws<NullReferenceException>(() => bm.Search("BA"u8.ToArray()));
    }

    /// <summary>
    /// Verifies that calling Search after Done throws a NullReferenceException.
    /// </summary>
    [Fact]
    public void Search_AfterDone_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        bm.Done();
        Assert.Throws<NullReferenceException>(() => bm.Search("BA"u8.ToArray()));
    }

    /// <summary>
    /// Verifies that passing a null text buffer to Search throws a NullReferenceException.
    /// </summary>
    [Fact]
    public void Search_WithNullText_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!));
    }

    /// <summary>
    /// Verifies that passing a null text buffer to the Search overload with offset throws a NullReferenceException.
    /// </summary>
    [Fact]
    public void Search_WithNullText_Overload_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!, 0, 1));
    }

    /// <summary>
    /// Verifies that a negative start index in the Search overload throws an IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void Search_Overload_NegativeStartIndex_Throws_IndexOutOfRangeException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<IndexOutOfRangeException>(() => bm.Search([0x41], -1, 1));
    }

    /// <summary>
    /// Verifies that a negative length parameter in the Search overload returns -1.
    /// </summary>
    [Fact]
    public void Search_Overload_NegativeLength_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search([0x41], 0, -1);
        Assert.Equal(-1, result);
    }

    /// <summary>
    /// Verifies that a start index beyond the text length throws an IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void Search_Overload_StartBeyondTextLength_Throws()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        Assert.Throws<IndexOutOfRangeException>(() => bm.Search("AB"u8.ToArray(), 2, 1));
    }

    /// <summary>
    /// Verifies that a zero-length search range returns -1.
    /// </summary>
    [Fact]
    public void Search_Overload_LengthZero_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search("AB"u8.ToArray(), 0, 0);
        Assert.Equal(-1, result);
    }

    /// <summary>
    /// Verifies that Search on an empty text buffer returns -1.
    /// </summary>
    [Fact]
    public void Search_EmptyText_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search([]);
        Assert.Equal(-1, result);
    }

    /// <summary>
    /// Verifies that calling Init twice does not throw and subsequent search still works correctly.
    /// </summary>
    [Fact]
    public void Init_CalledTwice_DoesNotThrow()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        bm.Init();

        var result = bm.Search("\0AB\0"u8.ToArray());
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Verifies that reinitializing with a new pattern after Done produces correct search results.
    /// </summary>
    [Fact]
    public void Reinit_AfterDone_Works()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        Assert.Equal(0, bm.Search("AB"u8.ToArray()));
        bm.Done();

        bm = new BoyerMoore("BC"u8.ToArray());
        bm.Init();
        Assert.Equal(0, bm.Search("BC"u8.ToArray()));
    }

    /// <summary>
    /// Verifies that the constructor accepting a custom alphabet size (128) works correctly.
    /// </summary>
    [Fact]
    public void Constructor_CustomAlphabetSize_Works()
    {
        var bm = new BoyerMoore([0x10, 0x20], 128);
        bm.Init();

        var result = bm.Search([0x10, 0x20, 0x30]);
        Assert.Equal(0, result);
    }

    /// <summary>
    /// Verifies that the constructor accepting a small custom alphabet size (16) works correctly.
    /// </summary>
    [Fact]
    public void Constructor_CustomAlphabetSize_16()
    {
        var bm = new BoyerMoore([0x05, 0x0A], 16);
        bm.Init();

        var result = bm.Search([0x01, 0x05, 0x0A]);
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Verifies that the Search overload returns -1 when the pattern is longer than the specified text range.
    /// </summary>
    [Fact]
    public void Search_PatternLongerThanTextOverload_ReturnsNegative()
    {
        var bm = new BoyerMoore("ABCD"u8.ToArray());
        bm.Init();

        var result = bm.Search("AB"u8.ToArray(), 0, 2);
        Assert.Equal(-1, result);
    }

    /// <summary>
    /// Verifies that searching for a single byte pattern returns -1 when no match exists.
    /// </summary>
    [Fact]
    public void Search_SingleBytePattern_NoMatch_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        Assert.Equal(-1, bm.Search("BCD"u8.ToArray()));
    }

    /// <summary>
    /// Verifies that the Search overload with range limits only finds matches within the specified window.
    /// </summary>
    [Fact]
    public void Search_Overload_MatchesOnlyWithinRange()
    {
        var bm = new BoyerMoore([0x42]);
        bm.Init();

        var text = "BBBB"u8.ToArray();

        var result = bm.Search(text, 2, 2);
        Assert.Equal(2, result);
    }

    /// <summary>
    /// Verifies that the Search overload returns -1 when a pattern exists earlier in the text but outside the specified range.
    /// </summary>
    [Fact]
    public void Search_Overload_PatternEarlier_OutOfRange_ReturnsNegative()
    {
        var bm = new BoyerMoore("AA"u8.ToArray());
        bm.Init();

        var text = "AAB"u8.ToArray();
        var result = bm.Search(text, 1, 2);
        Assert.Equal(-1, result);
    }
}
