namespace ExtractXiso.Tests;

public class BoyerMooreEdgeCasesTests
{
    [Fact]
    public void Constructor_ZeroLengthPattern_ReturnsZeroOnEmptySearch()
    {
        var bm = new BoyerMoore(new byte[0]);
        bm.Init();

        int result = bm.Search(new byte[] { 0x41, 0x42, 0x43 });
        Assert.Equal(0, result);
    }

    [Fact]
    public void Constructor_ZeroLengthPattern_InitThenDone_DoesNotThrow()
    {
        var bm = new BoyerMoore(new byte[0]);
        bm.Init();
        bm.Done();
    }

    [Fact]
    public void Search_BeforeInit_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42 });
        Assert.Throws<NullReferenceException>(() => bm.Search(new byte[] { 0x42, 0x41 }));
    }

    [Fact]
    public void Search_AfterDone_Throws_WhenTablesNeeded()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42 });
        bm.Init();
        bm.Done();
        Assert.Throws<NullReferenceException>(() => bm.Search(new byte[] { 0x42, 0x41 }));
    }

    [Fact]
    public void Search_WithNullText_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!));
    }

    [Fact]
    public void Search_WithNullText_Overload_Throws_NullReferenceException()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();
        Assert.Throws<NullReferenceException>(() => bm.Search(null!, 0, 1));
    }

    [Fact]
    public void Search_Overload_NegativeStartIndex_Throws_IndexOutOfRangeException()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();
        Assert.Throws<IndexOutOfRangeException>(() => bm.Search(new byte[] { 0x41 }, -1, 1));
    }

    [Fact]
    public void Search_Overload_NegativeLength_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        int result = bm.Search(new byte[] { 0x41 }, 0, -1);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_Overload_StartBeyondTextLength_Throws()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        Assert.Throws<IndexOutOfRangeException>(() => bm.Search(new byte[] { 0x41, 0x42 }, 2, 1));
    }

    [Fact]
    public void Search_Overload_LengthZero_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        int result = bm.Search(new byte[] { 0x41, 0x42 }, 0, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_EmptyText_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        int result = bm.Search(new byte[0]);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Init_CalledTwice_DoesNotThrow()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42 });
        bm.Init();
        bm.Init();

        int result = bm.Search(new byte[] { 0x00, 0x41, 0x42, 0x00 });
        Assert.Equal(1, result);
    }

    [Fact]
    public void Reinit_AfterDone_Works()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42 });
        bm.Init();
        Assert.Equal(0, bm.Search(new byte[] { 0x41, 0x42 }));
        bm.Done();

        bm = new BoyerMoore(new byte[] { 0x42, 0x43 });
        bm.Init();
        Assert.Equal(0, bm.Search(new byte[] { 0x42, 0x43 }));
    }

    [Fact]
    public void Constructor_CustomAlphabetSize_Works()
    {
        var bm = new BoyerMoore(new byte[] { 0x10, 0x20 }, 128);
        bm.Init();

        int result = bm.Search(new byte[] { 0x10, 0x20, 0x30 });
        Assert.Equal(0, result);
    }

    [Fact]
    public void Constructor_CustomAlphabetSize_16()
    {
        var bm = new BoyerMoore(new byte[] { 0x05, 0x0A }, 16);
        bm.Init();

        int result = bm.Search(new byte[] { 0x01, 0x05, 0x0A });
        Assert.Equal(1, result);
    }

    [Fact]
    public void Search_PatternLongerThanTextOverload_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42, 0x43, 0x44 });
        bm.Init();

        int result = bm.Search(new byte[] { 0x41, 0x42 }, 0, 2);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Search_SingleBytePattern_NoMatch_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        Assert.Equal(-1, bm.Search(new byte[] { 0x42, 0x43, 0x44 }));
    }

    [Fact]
    public void Search_Overload_MatchesOnlyWithinRange()
    {
        var bm = new BoyerMoore(new byte[] { 0x42 });
        bm.Init();

        var text = new byte[] { 0x42, 0x42, 0x42, 0x42 };

        int result = bm.Search(text, 2, 2);
        Assert.Equal(2, result);
    }

    [Fact]
    public void Search_Overload_PatternEarlier_OutOfRange_ReturnsNegative()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x41 });
        bm.Init();

        var text = new byte[] { 0x41, 0x41, 0x42 };
        int result = bm.Search(text, 1, 2);
        Assert.Equal(-1, result);
    }
}
