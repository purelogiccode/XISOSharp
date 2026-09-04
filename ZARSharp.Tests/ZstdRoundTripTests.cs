using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 0 harness skeleton: <c>decode(encode(x)) == x</c> over sizes × data kinds.
/// Red until Phase 6 (encoder); kept <c>Skip</c>ped so the suite stays green.
/// Sizes: {0,1,7,255,256,1023,4096,32768,65535,65536}.
/// Kinds: {zeros, text, random, sparse, repeated-pattern}.
/// </summary>
public sealed class ZstdRoundTripTests
{
    public static readonly int[] Sizes = [0, 1, 7, 255, 256, 1023, 4096, 32768, 65535, 65536];

    private static byte[] Zeros(int n)
    {
        return new byte[n];
    }

    private static byte[] Text(int n)
    {
        const string sample = "The quick brown fox jumps over the lazy dog. ZArchive block 64 KiB. ";
        var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        var outBuf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            outBuf[i] = ascii[i % ascii.Length];
        }

        return outBuf;
    }

    private static byte[] Random(int n, int seed)
    {
        var outBuf = new byte[n];
        var rnd = new Random(seed);
        rnd.NextBytes(outBuf);
        return outBuf;
    }

    private static byte[] Sparse(int n, int seed)
    {
        var outBuf = new byte[n];
        var rnd = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            outBuf[i] = rnd.Next(0, 16) == 0 ? (byte)rnd.Next(1, 256) : (byte)0;
        }

        return outBuf;
    }

    private static byte[] RepeatedPattern(int n)
    {
        byte[] pat = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var outBuf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            outBuf[i] = pat[i % pat.Length];
        }

        return outBuf;
    }

    public static TheoryData<int, string> Matrix()
    {
        var data = new TheoryData<int, string>();
        foreach (var s in Sizes)
        {
            data.Add(s, "zeros");
            data.Add(s, "text");
            data.Add(s, "random");
            data.Add(s, "sparse");
            data.Add(s, "pattern");
        }

        return data;
    }

    private static byte[] MakeKind(int size, string kind)
    {
        return kind switch
        {
            "zeros" => Zeros(size),
            "text" => Text(size),
            "random" => Random(size, 1000 + size),
            "sparse" => Sparse(size, 2000 + size),
            "pattern" => RepeatedPattern(size),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void DecodeEncode_RoundTrip(int size, string kind)
    {
        var input = MakeKind(size, kind);
        var compressor = new ZstdCompressor();
        var frame = compressor.CompressBlock(input);
        var decoded = ZstdDecompressor.Decompress(frame);
        Assert.Equal(input, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void DecodeEncode_RoundTrip_Levels(int level)
    {
        var input = Text(65536);
        var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
        var frame = compressor.CompressBlock(input);
        Assert.Equal(input, ZstdDecompressor.Decompress(frame));
    }

    [Fact]
    public void Harness_DataKinds_Sanity()
    {
        // Guard the harness itself (runs green before Phase 6).
        Assert.Empty(MakeKind(0, "zeros"));
        Assert.Single(MakeKind(1, "text"));
        Assert.Equal(256, MakeKind(256, "random").Length);
        Assert.Equal(65536, MakeKind(65536, "pattern").Length);
        Assert.Equal(1023, MakeKind(1023, "sparse").Length);
        Assert.Equal(65824, ZstdCompressor.GetCompressBound(65536));
    }
}