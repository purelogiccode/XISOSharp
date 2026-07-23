namespace XISOSharp.Tests;

public class BoyerMooreEdgeCasesTests
{
    [Fact]
    public void Constructor_ZeroLengthPattern_ReturnsZeroOnEmptySearch()
    {
        var bm = new BoyerMoore([]);
        bm.Init();

        var result = bm.Search("ABC"u8.ToArray());
        Assert.Equal(0, result);
    }

    [Fact]
    public void Constructor_ZeroLengthPattern_InitThenDone_DoesNotThrow()
    {
        var bm = new BoyerMoore([]);
        bm.Init();
        bm.Done();
    }

    [Fact]
    public void Search_BeforeInit_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        Assert.Throws<NullReferenceException>(() => bm.Search("BA"u8.ToArray()));
    }

    [Fact]
    public void Search_AfterDone_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        bm.Done();
        Assert.Throws<NullReferenceException>(() => bm.Search("BA"u8.ToArray()));
    }

    [Fact]
    public void Search_WithNullText_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!));
    }

    [Fact]
    public void Search_WithNullText_Overload_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!, 0, 1));
    }

    [Fact]
    public void Search_Overload_NegativeStartIndex_Throws_IndexOutOfRangeException()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();
        Assert.Throws<IndexOutOfRangeException>(() => bm.Search([0x41], -1, 1));
    }

    [Fact]
    public void Search_Overload_NegativeLength_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search([0x41], 0, -1);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_Overload_StartBeyondTextLength_Throws()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        Assert.Throws<IndexOutOfRangeException>(() => bm.Search("AB"u8.ToArray(), 2, 1));
    }

    [Fact]
    public void Search_Overload_LengthZero_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search("AB"u8.ToArray(), 0, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_EmptyText_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search([]);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Init_CalledTwice_DoesNotThrow()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        bm.Init();

        var result = bm.Search("\0AB\0"u8.ToArray());
        Assert.Equal(1, result);
    }

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

    [Fact]
    public void Constructor_CustomAlphabetSize_Works()
    {
        var bm = new BoyerMoore([0x10, 0x20], 128);
        bm.Init();

        var result = bm.Search([0x10, 0x20, 0x30]);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Constructor_CustomAlphabetSize_16()
    {
        var bm = new BoyerMoore([0x05, 0x0A], 16);
        bm.Init();

        var result = bm.Search([0x01, 0x05, 0x0A]);
        Assert.Equal(1, result);
    }

    [Fact]
    public void Search_PatternLongerThanTextOverload_ReturnsNegative()
    {
        var bm = new BoyerMoore("ABCD"u8.ToArray());
        bm.Init();

        var result = bm.Search("AB"u8.ToArray(), 0, 2);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_SingleBytePattern_NoMatch_ReturnsNegative()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        Assert.Equal(-1, bm.Search("BCD"u8.ToArray()));
    }

    [Fact]
    public void Search_Overload_MatchesOnlyWithinRange()
    {
        var bm = new BoyerMoore([0x42]);
        bm.Init();

        var text = "BBBB"u8.ToArray();

        var result = bm.Search(text, 2, 2);
        Assert.Equal(2, result);
    }

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
