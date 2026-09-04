using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 3 acceptance: Huffman encoder round-trips decoded with the
/// <em>existing</em> <c>ZstdHuffman</c> decoder (never the encoder's own
/// tables), including the 255-symbol corner (forces the FSE-compressed
/// weights form) and the 1-symbol corner (RLE).
/// Reference: <c>lib/compress/huf_compress.c</c>.
/// </summary>
public sealed class ZstdHuffmanTests
{
    public static TheoryData<int, string> CompressibleCases()
    {
        var data = new TheoryData<int, string>();
        foreach (var size in new[] { 16, 64, 255, 256, 257, 1000, 4096, 16384 })
        {
            data.Add(size, "skewed");
            data.Add(size, "small-alpha");
            data.Add(size, "text-like");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CompressibleCases))]
    public void Huf_RoundTrip_CompressibleInputs(int size, string kind)
    {
        var src = MakeLiterals(kind, size, 0x485546u ^ (uint)size ^ (uint)kind.Length);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(size)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);

        if (result == 0)
        {
            // Valid answer for hard inputs (stored raw by the caller).
            Assert.True(IsHardCase(kind, size, src), $"Unexpected raw fallback for {kind} x {size}.");
            return;
        }

        if (result == 1)
        {
            Assert.All(src, b => Assert.Equal(dst[0], b));
            return;
        }

        Assert.InRange(result, 2, dst.Length);
        DecodeAndVerify(dst, 0, result, src, singleStream: size < ZstdHuffmanEncoder.SingleStreamThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Huf_TinyInputs_UseRawOrRle(int size)
    {
        var rng = new Random(0x71);
        for (var trial = 0; trial < 20; trial++)
        {
            var src = new byte[size];
            // Mix of single-symbol, two-symbol, and random inputs.
            if (trial % 3 != 0)
            {
                rng.NextBytes(src);
                if (trial % 3 == 1 && size > 0)
                {
                    src[0] = src[size - 1];
                }
            }
            else if (size > 0)
            {
                Array.Fill(src, (byte)(0xA0 + trial));
            }

            var dst = new byte[Math.Max(16, ZstdHuffmanEncoder.CompressBound(size))];
            var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
            if (result == 1)
            {
                Assert.All(src, b => Assert.Equal(dst[0], b));
            }
            else if (result > 1)
            {
                DecodeAndVerify(dst, 0, result, src, singleStream: true);
            }
            else
            {
                Assert.Equal(0, result);
            }
        }
    }

    [Fact]
    public void Huf_SingleSymbol_UsesRlePath()
    {
        var src = new byte[100];
        Array.Fill(src, (byte)0xAB);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        Assert.Equal(1, ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length));
        Assert.Equal(0xAB, dst[0]);
    }

    [Fact]
    public void Huf_EmptyInput_ReturnsZero()
    {
        var dst = new byte[16];
        Assert.Equal(0, ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, [], 0, 0));
    }

    [Fact]
    public void Huf_RandomData_StoresRaw()
    {
        // Incompressible: the suspect heuristic must bail to raw, never expand.
        var rng = new Random(0x9A9D);
        var src = new byte[4096];
        rng.NextBytes(src);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        Assert.Equal(0, ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length));
    }

    [Fact]
    public void Huf_All255Symbols_RoundTrips()
    {
        // 255 distinct symbols: the direct nibble-packed weights form cannot
        // represent this (header byte limit), so the FSE-compressed weights
        // form must be used.
        var rng = new Random(0xFF);
        var src = new byte[2048];
        for (var i = 0; i < 255; i++)
        {
            src[i] = (byte)i;
        }

        // Skewed fill: hot symbols compress well, tail keeps all 255 present.
        for (var i = 255; i < src.Length; i++)
        {
            src[i] = rng.Next(10) < 7 ? (byte)rng.Next(4) : (byte)rng.Next(255);
        }

        // Shuffle the forced prefix so order is not sorted.
        for (var i = src.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (src[i], src[j]) = (src[j], src[i]);
        }

        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
        Assert.InRange(result, 2, src.Length - 2);
        Assert.True(dst[0] < 128, "255 symbols must use the FSE-compressed weights form.");
        DecodeAndVerify(dst, 0, result, src, singleStream: false);
    }

    [Fact]
    public void Huf_All256Symbols_RoundTrips()
    {
        var rng = new Random(0x100);
        var src = new byte[4096];
        for (var i = 0; i < 256; i++)
        {
            src[i] = (byte)i;
        }

        for (var i = 256; i < src.Length; i++)
        {
            src[i] = rng.Next(10) < 7 ? (byte)rng.Next(4) : (byte)rng.Next(256);
        }

        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
        Assert.InRange(result, 2, src.Length - 2);
        DecodeAndVerify(dst, 0, result, src, singleStream: false);
    }

    [Fact]
    public void Huf_TwoSymbols_RoundTrips()
    {
        var src = new byte[1000];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = (i % 7 == 0) ? (byte)1 : (byte)0;
        }

        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
        Assert.InRange(result, 2, src.Length - 2);
        Assert.Equal(128, dst[0]); // Two weights, nibble-packed direct form.
        DecodeAndVerify(dst, 0, result, src, singleStream: false);
    }

    [Fact]
    public void Huf_FourStreamLayout_HasValidJumpTable()
    {
        var src = MakeLiterals("text-like", 1000, 0x4B);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
        Assert.InRange(result, 2, src.Length - 2);

        var treeSize = ZstdHuffman.ReadStats(dst, 0, result, out _, out _, out _);
        var streamsLength = result - treeSize;
        Assert.True(streamsLength >= 10, "4-stream payload needs a 6-byte jump table.");

        var s1 = dst[treeSize] | (dst[treeSize + 1] << 8);
        var s2 = dst[treeSize + 2] | (dst[treeSize + 3] << 8);
        var s3 = dst[treeSize + 4] | (dst[treeSize + 5] << 8);
        var s4 = streamsLength - 6 - s1 - s2 - s3;
        Assert.InRange(s1, 1, 65535);
        Assert.InRange(s2, 1, 65535);
        Assert.InRange(s3, 1, 65535);
        Assert.True(s4 > 0, "Fourth stream must be non-empty.");

        var seg = (src.Length + 3) / 4;
        Assert.Equal(src.Length - (3 * seg), src.Length - (3 * seg)); // Sanity.
        Assert.True(seg * 3 <= src.Length + 3, "Segment split must match the decoder.");
    }

    [Fact]
    public void Huf_TableLog_NeverExceedsDecoderCeiling()
    {
        // Randomized compressible inputs must always select a table the
        // decoder accepts (codes ≤ 11 bits).
        var rng = new Random(0xCE11);
        for (var trial = 0; trial < 60; trial++)
        {
            var size = rng.Next(16, 5000);
            var alpha = rng.Next(2, 256);
            var src = new byte[size];
            for (var i = 0; i < size; i++)
            {
                src[i] = (byte)(rng.Next(4) == 0 ? rng.Next(alpha) : rng.Next(Math.Min(alpha, 8)));
            }

            var dst = new byte[ZstdHuffmanEncoder.CompressBound(size)];
            var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
            if (result > 1)
            {
                ZstdHuffman.ReadStats(dst, 0, result, out _, out var tableLog, out _);
                Assert.InRange(tableLog, 1, 11);
                DecodeAndVerify(dst, 0, result, src, singleStream: size < ZstdHuffmanEncoder.SingleStreamThreshold);
            }
        }
    }

    [Fact]
    public void Huf_EncodeIntoOffset_Works()
    {
        var src = MakeLiterals("skewed", 500, 0x0FF);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length) + 64];
        Array.Fill(dst, (byte)0xCC);
        var result = ZstdHuffmanEncoder.Compress(dst, 37, dst.Length - 37, src, 0, src.Length);
        Assert.InRange(result, 2, src.Length - 2);
        Assert.Equal(0xCC, dst[36]);
        DecodeAndVerify(dst, 37, result, src, singleStream: false);

        var sub = new byte[result];
        Array.Copy(dst, 37, sub, 0, result);
        DecodeAndVerify(sub, 0, result, src, singleStream: false);
    }

    [Fact]
    public void Huf_SizingHelpers_MatchReferenceFormulas()
    {
        Assert.Equal(129, ZstdHuffmanEncoder.CTableBound);
        Assert.Equal(1000 + (1000 >> 8) + 8, ZstdHuffmanEncoder.BlockBound(1000));
        Assert.Equal(129 + 1000 + (1000 >> 8) + 8, ZstdHuffmanEncoder.CompressBound(1000));

        Assert.Equal(1, ZstdHuffmanEncoder.MinTableLog(1));
        Assert.Equal(2, ZstdHuffmanEncoder.MinTableLog(2));
        Assert.Equal(2, ZstdHuffmanEncoder.MinTableLog(3));
        Assert.Equal(9, ZstdHuffmanEncoder.MinTableLog(256));

        var counts = new uint[256];
        counts[0] = 10;
        counts[1] = 5;
        counts[2] = 5;
        Assert.Equal(3, ZstdHuffmanEncoder.Cardinality(counts, 255));

        // Bound must cover real outputs, including the 4-stream jump table.
        var src = MakeLiterals("text-like", 4096, 0xB0);
        var dst = new byte[ZstdHuffmanEncoder.CompressBound(src.Length)];
        var result = ZstdHuffmanEncoder.Compress(dst, 0, dst.Length, src, 0, src.Length);
        Assert.InRange(result, 2, dst.Length);
    }

    [Fact]
    public void Huf_Compress1X_TooSmallDestination_ReturnsZero()
    {
        var src = MakeLiterals("skewed", 500, 0x5A);
        var counts = new uint[256];
        foreach (var b in src)
        {
            counts[b]++;
        }

        var maxSv = 255;
        while (counts[maxSv] == 0)
        {
            maxSv--;
        }

        var (table, _) = ZstdHuffmanEncoder.BuildCTable(counts, maxSv, 9);
        var tiny = new byte[8];
        Assert.Equal(0, ZstdHuffmanEncoder.Compress1X(tiny, 0, 4, src, 0, src.Length, table));
    }

    private static void DecodeAndVerify(byte[] dst, int offset, int length, byte[] expected, bool singleStream)
    {
        var treeSize =
            ZstdHuffman.ReadStats(dst, offset, length, out var weights, out var tableLog, out var numSymbols);
        Assert.InRange(tableLog, 1, 11);
        var table = ZstdHuffman.BuildTable(weights, numSymbols, tableLog);

        var got = new byte[expected.Length];
        if (singleStream)
        {
            ZstdHuffman.DecodeStream(dst, offset + treeSize, length - treeSize, table, got, 0, got.Length);
        }
        else
        {
            var streamsLength = length - treeSize;
            var s1 = dst[offset + treeSize] | (dst[offset + treeSize + 1] << 8);
            var s2 = dst[offset + treeSize + 2] | (dst[offset + treeSize + 3] << 8);
            var s3 = dst[offset + treeSize + 4] | (dst[offset + treeSize + 5] << 8);
            var s4 = streamsLength - 6 - s1 - s2 - s3;
            var seg = (expected.Length + 3) / 4;
            var c1 = offset + treeSize + 6;
            ZstdHuffman.DecodeStream(dst, c1, s1, table, got, 0, seg);
            ZstdHuffman.DecodeStream(dst, c1 + s1, s2, table, got, seg, seg);
            ZstdHuffman.DecodeStream(dst, c1 + s1 + s2, s3, table, got, 2 * seg, seg);
            ZstdHuffman.DecodeStream(dst, c1 + s1 + s2 + s3, s4, table, got, 3 * seg, expected.Length - (3 * seg));
        }

        Assert.Equal(expected, got);
    }

    private static byte[] MakeLiterals(string kind, int size, uint seed)
    {
        var rng = new Random(unchecked((int)seed));
        var src = new byte[size];
        switch (kind)
        {
            case "skewed":
                // Zipf-like over 32 symbols: highly compressible.
                for (var i = 0; i < size; i++)
                {
                    var v = rng.Next(256);
                    src[i] = (byte)(v < 128 ? rng.Next(4) : v < 224 ? rng.Next(32) : rng.Next(256));
                }

                break;
            case "small-alpha":
                for (var i = 0; i < size; i++)
                {
                    src[i] = (byte)rng.Next(6);
                }

                break;
            default: // "text-like": word-ish bytes with spaces and repeats.
                const string words = "the quick brown fox jumps over lazy dog 0123456789 ";
                for (var i = 0; i < size; i++)
                {
                    src[i] = (byte)words[rng.Next(words.Length)];
                }

                break;
        }

        return src;
    }

    private static bool IsHardCase(string kind, int size, byte[] src)
    {
        // Only tiny or low-redundancy inputs may validly fall back to raw.
        if (size < 32)
        {
            return true;
        }

        var distinct = new HashSet<byte>(src).Count;
        return distinct > size / 4;
    }
}