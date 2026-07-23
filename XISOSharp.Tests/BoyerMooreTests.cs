using XISOSharp;

namespace XISOSharp.Tests;

public class BoyerMooreTests
{
    [Fact]
    public void Init_SetsUpTables()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42, 0x43 }); // "ABC"
        bm.Init();
        // Should not throw
        bm.Done();
    }

    [Fact]
    public void Search_FindsPatternAtStart()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42, 0x43 }); // "ABC"
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x44, 0x45 }; // ABCDE
        int result = bm.Search(text);

        Assert.Equal(0, result);
        bm.Done();
    }

    [Fact]
    public void Search_FindsPatternInMiddle()
    {
        var bm = new BoyerMoore(new byte[] { 0x42, 0x43 }); // "BC"
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x44 }; // ABCD
        int result = bm.Search(text);

        Assert.Equal(1, result);
        bm.Done();
    }

    [Fact]
    public void Search_FindsPatternAtEnd()
    {
        var bm = new BoyerMoore(new byte[] { 0x43, 0x44 }); // "CD"
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x44 }; // ABCD
        int result = bm.Search(text);

        Assert.Equal(2, result);
        bm.Done();
    }

    [Fact]
    public void Search_NoMatch_ReturnsMinusOne()
    {
        var bm = new BoyerMoore(new byte[] { 0x58, 0x59 }); // "XY"
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x44 }; // ABCD
        int result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    [Fact]
    public void Search_EmptyText_ReturnsMinusOne()
    {
        var bm = new BoyerMoore(new byte[] { 0x41 });
        bm.Init();

        int result = bm.Search(Array.Empty<byte>());

        Assert.Equal(-1, result);
        bm.Done();
    }

    [Fact]
    public void Search_PatternLongerThanText_ReturnsMinusOne()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42, 0x43, 0x44 });
        bm.Init();

        byte[] text = { 0x41, 0x42 };
        int result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    [Fact]
    public void Search_FindsFirstOfMultipleMatches()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x41 }); // "AA"
        bm.Init();

        byte[] text = { 0x42, 0x41, 0x41, 0x41, 0x41, 0x43 };
        int result = bm.Search(text);

        Assert.Equal(1, result);
        bm.Done();
    }

    [Fact]
    public void Search_MediaEnablePattern_InBuffer()
    {
        byte[] pattern = { 0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D };
        var bm = new BoyerMoore(pattern);
        bm.Init();

        // Create buffer with pattern at offset 100
        byte[] text = new byte[200];
        Array.Fill(text, (byte)0x00);
        Array.Copy(pattern, 0, text, 100, pattern.Length);

        int result = bm.Search(text);

        Assert.Equal(100, result);
        bm.Done();
    }

    [Fact]
    public void Search_MediaEnablePattern_NotFound()
    {
        byte[] pattern = { 0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D };
        var bm = new BoyerMoore(pattern);
        bm.Init();

        byte[] text = new byte[1000];
        // All zeros, no pattern
        int result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    [Fact]
    public void Search_WithOffset_RespectsBoundary()
    {
        byte[] pattern = { 0x42, 0x43 }; // "BC"
        var bm = new BoyerMoore(pattern);
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x41, 0x42, 0x43 }; // ABCABC

        // Search from offset 0 with length 3: should find at index 1
        int result = bm.Search(text, 0, 3);
        Assert.Equal(1, result);

        // Search from offset 2 with length 4: should find at index 4
        result = bm.Search(text, 2, 4);
        Assert.Equal(4, result);

        bm.Done();
    }

    [Fact]
    public void Search_SingleBytePattern()
    {
        var bm = new BoyerMoore(new byte[] { 0xFF });
        bm.Init();

        byte[] text = { 0x00, 0x01, 0xFF, 0x02, 0xFF };
        int result = bm.Search(text);

        Assert.Equal(2, result);
        bm.Done();
    }

    [Fact]
    public void Search_MediaEnablePattern_In1000ByteBuffer()
    {
        byte[] pattern = Constants.MediaEnable;
        var bm = new BoyerMoore(pattern);
        bm.Init();

        byte[] text = new byte[1000];
        // Fill with pattern-like data that almost matches
        text[50] = 0xE8;
        text[51] = 0xCA;
        text[52] = 0xFD;
        text[53] = 0xFF;
        text[54] = 0xFF;
        text[55] = 0x85;
        text[56] = 0xC0;
        text[57] = 0x7D; // Full match at offset 50

        // Another partial match at offset 200
        text[200] = 0xE8;
        text[201] = 0xCA;
        text[202] = 0xFD;
        text[203] = 0xFF;
        text[204] = 0xFF;
        text[205] = 0x85;
        text[206] = 0xC0;
        text[207] = 0x7D; // Full match at offset 200

        int result = bm.Search(text);

        Assert.Equal(50, result);
        bm.Done();
    }

    [Fact]
    public void Search_PatternWithRepeatingBytes()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x41, 0x41 });
        bm.Init();

        byte[] text = { 0x41, 0x41, 0x41, 0x41, 0x41 };
        int result = bm.Search(text);

        Assert.Equal(0, result);
        bm.Done();
    }

    [Fact]
    public void Init_ThenDone_CanReinit()
    {
        var bm = new BoyerMoore(new byte[] { 0x41, 0x42 });
        bm.Init();
        bm.Done();

        // Re-init with different pattern
        bm = new BoyerMoore(new byte[] { 0x43, 0x44 });
        bm.Init();

        byte[] text = { 0x41, 0x42, 0x43, 0x44 };
        int result = bm.Search(text);
        Assert.Equal(2, result);
        bm.Done();
    }
}
