namespace XISOSharp.Tests;

/// <summary>
/// Tests for the BoyerMoore byte-pattern search algorithm.
/// </summary>
public class BoyerMooreTests
{
    /// <summary>
    /// Verifies that Init sets up internal tables without throwing an exception.
    /// </summary>
    [Fact]
    public void Init_SetsUpTables()
    {
        var bm = new BoyerMoore("ABC"u8.ToArray()); // "ABC"
        bm.Init();
        // Should not throw
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search finds a pattern located at the start of the text.
    /// </summary>
    [Fact]
    public void Search_FindsPatternAtStart()
    {
        var bm = new BoyerMoore("ABC"u8.ToArray()); // "ABC"
        bm.Init();

        var text = "ABCDE"u8.ToArray(); // ABCDE
        var result = bm.Search(text);

        Assert.Equal(0, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search finds a pattern located in the middle of the text.
    /// </summary>
    [Fact]
    public void Search_FindsPatternInMiddle()
    {
        var bm = new BoyerMoore("BC"u8.ToArray()); // "BC"
        bm.Init();

        var text = "ABCD"u8.ToArray(); // ABCD
        var result = bm.Search(text);

        Assert.Equal(1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search finds a pattern located at the end of the text.
    /// </summary>
    [Fact]
    public void Search_FindsPatternAtEnd()
    {
        var bm = new BoyerMoore("CD"u8.ToArray()); // "CD"
        bm.Init();

        var text = "ABCD"u8.ToArray(); // ABCD
        var result = bm.Search(text);

        Assert.Equal(2, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search returns -1 when the pattern is not present in the text.
    /// </summary>
    [Fact]
    public void Search_NoMatch_ReturnsMinusOne()
    {
        var bm = new BoyerMoore("XY"u8.ToArray()); // "XY"
        bm.Init();

        var text = "ABCD"u8.ToArray(); // ABCD
        var result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search returns -1 when given an empty text buffer.
    /// </summary>
    [Fact]
    public void Search_EmptyText_ReturnsMinusOne()
    {
        var bm = new BoyerMoore([0x41]);
        bm.Init();

        var result = bm.Search([]);

        Assert.Equal(-1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search returns -1 when the pattern is longer than the text.
    /// </summary>
    [Fact]
    public void Search_PatternLongerThanText_ReturnsMinusOne()
    {
        var bm = new BoyerMoore("ABCD"u8.ToArray());
        bm.Init();

        var text = "AB"u8.ToArray();
        var result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search returns the index of the first occurrence when multiple matches exist.
    /// </summary>
    [Fact]
    public void Search_FindsFirstOfMultipleMatches()
    {
        var bm = new BoyerMoore("AA"u8.ToArray()); // "AA"
        bm.Init();

        var text = "BAAAAC"u8.ToArray();
        var result = bm.Search(text);

        Assert.Equal(1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search finds the MediaEnable byte pattern placed at a known offset within a buffer.
    /// </summary>
    [Fact]
    public void Search_MediaEnablePattern_InBuffer()
    {
        byte[] pattern = [0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D];
        var bm = new BoyerMoore(pattern);
        bm.Init();

        // Create buffer with pattern at offset 100
        var text = new byte[200];
        Array.Fill(text, (byte)0x00);
        Array.Copy(pattern, 0, text, 100, pattern.Length);

        var result = bm.Search(text);

        Assert.Equal(100, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search returns -1 when the MediaEnable pattern is not present in the buffer.
    /// </summary>
    [Fact]
    public void Search_MediaEnablePattern_NotFound()
    {
        byte[] pattern = [0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D];
        var bm = new BoyerMoore(pattern);
        bm.Init();

        var text = new byte[1000];
        // All zeros, no pattern
        var result = bm.Search(text);

        Assert.Equal(-1, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that the Search overload with start index and length respects the specified range boundaries.
    /// </summary>
    [Fact]
    public void Search_WithOffset_RespectsBoundary()
    {
        var pattern = "BC"u8.ToArray(); // "BC"
        var bm = new BoyerMoore(pattern);
        bm.Init();

        var text = "ABCABC"u8.ToArray(); // ABCABC

        // Search from offset 0 with length 3: should find at index 1
        var result = bm.Search(text, 0, 3);
        Assert.Equal(1, result);

        // Search from offset 2 with length 4: should find at index 4
        result = bm.Search(text, 2, 4);
        Assert.Equal(4, result);

        bm.Done();
    }

    /// <summary>
    /// Verifies that Search correctly finds a single-byte pattern in a buffer with multiple occurrences.
    /// </summary>
    [Fact]
    public void Search_SingleBytePattern()
    {
        var bm = new BoyerMoore([0xFF]);
        bm.Init();

        byte[] text = [0x00, 0x01, 0xFF, 0x02, 0xFF];
        var result = bm.Search(text);

        Assert.Equal(2, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search finds the first occurrence of the MediaEnable pattern in a 1000-byte buffer when multiple matches exist.
    /// </summary>
    [Fact]
    public void Search_MediaEnablePattern_In1000ByteBuffer()
    {
        var pattern = Constants.MediaEnable;
        var bm = new BoyerMoore(pattern);
        bm.Init();

        var text = new byte[1000];
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

        var result = bm.Search(text);

        Assert.Equal(50, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that Search correctly handles a pattern consisting of repeating identical bytes.
    /// </summary>
    [Fact]
    public void Search_PatternWithRepeatingBytes()
    {
        var bm = new BoyerMoore("AAA"u8.ToArray());
        bm.Init();

        var text = "AAAAA"u8.ToArray();
        var result = bm.Search(text);

        Assert.Equal(0, result);
        bm.Done();
    }

    /// <summary>
    /// Verifies that calling Done then reinitializing with a new pattern on a new instance works correctly.
    /// </summary>
    [Fact]
    public void Init_ThenDone_CanReinit()
    {
        var bm = new BoyerMoore("AB"u8.ToArray());
        bm.Init();
        bm.Done();

        // Re-init with different pattern
        bm = new BoyerMoore("CD"u8.ToArray());
        bm.Init();

        var text = "ABCD"u8.ToArray();
        var result = bm.Search(text);
        Assert.Equal(2, result);
        bm.Done();
    }
}