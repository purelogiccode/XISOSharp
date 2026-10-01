using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for the internal <see cref="Latin1Encoding"/>: full byte-range
/// round-trips, counting consistency, out-of-range rejection, and buffer guards.
/// </summary>
public class Latin1EncodingExtraTests
{
    private static readonly Encoding Encoding = Latin1Encoding.Instance;

    /// <summary>Verifies every byte value 0x00-0xFF round-trips through chars and back.</summary>
    [Fact]
    public void RoundTrip_AllByteValues()
    {
        byte[] bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;

        char[] chars = new char[256];
        Assert.Equal(256, Encoding.GetChars(bytes, 0, 256, chars, 0));
        for (int i = 0; i < chars.Length; i++) Assert.Equal((char)i, chars[i]);

        byte[] back = new byte[256];
        Assert.Equal(256, Encoding.GetBytes(chars, 0, 256, back, 0));
        Assert.Equal(bytes, back);
    }

    /// <summary>Verifies byte counting equals character count for every overload.</summary>
    [Fact]
    public void GetByteCount_MatchesLength()
    {
        const string text = "caf\u00E9 \u00FF\u00FE";
        Assert.Equal(text.Length, Encoding.GetByteCount(text));
        Assert.Equal(text.Length, Encoding.GetByteCount(text.AsSpan()));
        Assert.Equal(text.Length, Encoding.GetByteCount(text.ToCharArray(), 0, text.Length));
    }

    /// <summary>Verifies characters outside Latin-1 are rejected while counting.</summary>
    [Theory]
    [InlineData("\u0100")]
    [InlineData("\u65E5")]
    public void GetByteCount_NonLatin1_Throws(string text)
    {
        Assert.Throws<ArgumentException>(() => Encoding.GetByteCount(text));
        Assert.Throws<ArgumentException>(() => Encoding.GetByteCount(text.AsSpan()));
    }

    /// <summary>Verifies characters outside Latin-1 are rejected while encoding.</summary>
    [Fact]
    public void GetBytes_NonLatin1_Throws()
    {
        char[] chars = ['a', '\u0100'];
        byte[] buffer = new byte[2];
        Assert.Throws<ArgumentException>(() => Encoding.GetBytes(chars, 0, 2, buffer, 0));
        Assert.Throws<ArgumentException>(() => Encoding.GetBytes(new string(chars), 0, 2, buffer, 0));
        Assert.Throws<ArgumentException>(() => Encoding.GetBytes(chars.AsSpan(), buffer.AsSpan()));
    }

    /// <summary>Verifies an undersized byte destination is rejected.</summary>
    [Fact]
    public void GetBytes_TooSmallDestination_Throws()
    {
        Assert.Throws<ArgumentException>(() => Encoding.GetBytes("abc".AsSpan(), new byte[2]));
    }

    /// <summary>Verifies an undersized char destination is rejected.</summary>
    [Fact]
    public void GetChars_TooSmallDestination_Throws()
    {
        Assert.Throws<ArgumentException>(() => Encoding.GetChars([1, 2, 3], new char[2]));
    }

    /// <summary>Verifies max-count estimates are the identity for Latin-1.</summary>
    [Fact]
    public void MaxCounts_AreIdentity()
    {
        Assert.Equal(5, Encoding.GetMaxByteCount(5));
        Assert.Equal(7, Encoding.GetMaxCharCount(7));
    }

    /// <summary>Verifies null inputs are rejected.</summary>
    [Fact]
    public void NullInputs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Encoding.GetByteCount((string)null!));
        Assert.Throws<ArgumentNullException>(() => Encoding.GetBytes((string)null!, 0, 0, [], 0));
        Assert.Throws<ArgumentNullException>(() => Encoding.GetChars(null!, 0, 0, [], 0));
    }
}
