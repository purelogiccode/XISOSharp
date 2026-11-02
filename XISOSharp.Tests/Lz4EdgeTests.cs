using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Additional edge-case tests for the managed <see cref="Lz4"/> block codec:
/// short inputs, argument validation, malformed blocks, overlapping matches,
/// and round-trips, complementing the golden-vector coverage in <c>CisoTests</c>.
/// </summary>
public class Lz4EdgeTests
{
    private static byte[] RoundTrip(byte[] input)
    {
        byte[] compressed = new byte[Lz4.MaxCompressedOutputSize(input.Length)];
        int written = Lz4.Compress(input, compressed);
        byte[] output = new byte[input.Length];
        int decompressed = Lz4.Decompress(compressed.AsSpan(0, written), output);
        Assert.Equal(input.Length, decompressed);
        return output;
    }

    /// <summary>Verifies inputs shorter than the match minimum round-trip as literals.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void Compress_ShortInput_RoundTrips(int length)
    {
        byte[] input = new byte[length];
        for (int i = 0; i < length; i++) input[i] = (byte)(i + 1);
        Assert.Equal(input, RoundTrip(input));
    }

    /// <summary>Verifies an undersized destination is rejected.</summary>
    [Fact]
    public void Compress_DestinationTooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() => Lz4.Compress([1, 2, 3], new byte[2]));
    }

    /// <summary>Verifies acceleration values below one are rejected.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Compress_InvalidAcceleration_Throws(int acceleration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4.Compress([1, 2, 3], new byte[64], acceleration));
    }

    /// <summary>Verifies the destination-size formula and its guards.</summary>
    [Fact]
    public void MaxCompressedOutputSize_FormulaAndGuards()
    {
        Assert.Equal(130, Lz4.MaxCompressedOutputSize(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4.MaxCompressedOutputSize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4.MaxCompressedOutputSize(int.MaxValue));
    }

    /// <summary>Verifies an empty source decompresses to zero bytes.</summary>
    [Fact]
    public void Decompress_EmptySource_ReturnsZero()
    {
        Assert.Equal(0, Lz4.Decompress([], new byte[4]));
    }

    /// <summary>Verifies truncated and zero-offset blocks are rejected.</summary>
    [Theory]
    [InlineData(new byte[] { 0xF0 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x10, 0x41, 0x00, 0x00 })]
    public void Decompress_Malformed_Throws(byte[] source)
    {
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(source, new byte[64]));
    }

    /// <summary>Verifies a destination that is too small for the literals is rejected.</summary>
    [Fact]
    public void Decompress_TooSmallDestination_Throws()
    {
        byte[] input = Encoding.ASCII.GetBytes("0123456789abcdefghij");
        byte[] compressed = new byte[Lz4.MaxCompressedOutputSize(input.Length)];
        int written = Lz4.Compress(input, compressed);
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(compressed.AsSpan(0, written), new byte[4]));
    }

    /// <summary>Verifies an overlapping match copies the repeated pattern correctly.</summary>
    [Fact]
    public void Decompress_OverlappingMatch_ExpandsPattern()
    {
        byte[] source = [0x40, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x04, 0x00];
        byte[] destination = new byte[8];
        Assert.Equal(8, Lz4.Decompress(source, destination));
        Assert.Equal("abcdabcd", Encoding.ASCII.GetString(destination));
    }

    /// <summary>Verifies arbitrary data round-trips through compress/decompress.</summary>
    [Fact]
    public void Compress_ArbitraryData_RoundTrips()
    {
        byte[] input = new byte[4096];
        Random random = new(12345);
        random.NextBytes(input);
        Assert.Equal(input, RoundTrip(input));
    }

    /// <summary>Verifies highly compressible data round-trips and compresses well.</summary>
    [Fact]
    public void Compress_ZeroBlock_RoundTripsAndShrinks()
    {
        byte[] input = new byte[8192];
        byte[] compressed = new byte[Lz4.MaxCompressedOutputSize(input.Length)];
        int written = Lz4.Compress(input, compressed);
        Assert.True(written < input.Length / 4, $"expected heavy compression, got {written} bytes");
        Assert.Equal(input, RoundTrip(input));
    }

    /// <summary>Verifies all-zero input is detected when using an explicit acceleration.</summary>
    [Fact]
    public void Compress_HighAcceleration_StillRoundTrips()
    {
        byte[] input = new byte[2048];
        byte[] compressed = new byte[Lz4.MaxCompressedOutputSize(input.Length)];
        int written = Lz4.Compress(input, compressed, acceleration: 12);
        byte[] output = new byte[input.Length];
        Assert.Equal(input.Length, Lz4.Decompress(compressed.AsSpan(0, written), output));
        Assert.Equal(input, output);
    }
}
