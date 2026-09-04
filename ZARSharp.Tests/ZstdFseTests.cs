using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 2 acceptance: FSE encoder round-trips for the alphabets zstd uses
/// (LL 36 symbols, ML 53, OF 29) across skewed / uniform / random
/// distributions and table sizes 2^6..2^9, decoded with the <em>existing</em>
/// <c>ZstdFse</c> decoder (never the encoder's own tables).
/// Reference: <c>lib/compress/fse_compress.c</c>.
/// </summary>
public sealed class ZstdFseTests
{
    // (alphabetSize, tableLog, useLowProbCount)
    public static TheoryData<int, int, bool> Contexts()
    {
        var data = new TheoryData<int, int, bool>();
        foreach (var alphabet in new[] { 36, 53, 29 })
        {
            foreach (var tableLog in new[] { 6, 7, 8, 9 })
            {
                data.Add(alphabet, tableLog, false);
                data.Add(alphabet, tableLog, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Fse_RoundTrip_AllDistributions(int alphabet, int tableLog, bool useLowProb)
    {
        string[] kinds = ["skewed", "uniform", "random"];
        for (var d = 0; d < kinds.Length; d++)
        {
            var symbols = MakeSymbols(kinds[d], alphabet, 3000, SeedFor(alphabet, tableLog, useLowProb, d));
            RoundTripOnce(symbols, alphabet, tableLog, useLowProb, kinds[d]);
        }
    }

    [Theory]
    [InlineData(36)]
    [InlineData(53)]
    [InlineData(29)]
    public void Fse_RleInput_UsesRlePath(int alphabet)
    {
        var zeros = new byte[64];
        var sevens = new byte[64];
        Array.Fill(sevens, (byte)7);

        foreach (var symbols in new[] { zeros, sevens })
        {
            var (counts, maxSv) = Histogram(symbols, alphabet);
            var norms = new short[alphabet];
            var total = symbols.Length;
            var tableLog = ZstdFseEncoder.OptimalTableLog(9, total, maxSv);
            Assert.Equal(-1, ZstdFseEncoder.NormalizeCounts(norms, counts, total, maxSv, tableLog, false));

            Assert.True(ZstdFseEncoder.IsSingleSymbol(symbols, 0, symbols.Length, out var sym));
            Assert.Equal(symbols[0], sym);

            var ct = ZstdFseEncoder.BuildCTable(new short[] { 63, 1 }, 1, 6);
            Assert.Equal(-1, ZstdFseEncoder.Encode(new byte[1024], 0, 1024, symbols, 0, symbols.Length, ct));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(64)]
    public void Fse_RoundTrip_SmallStreams(int length)
    {
        const int alphabet = 8;
        var symbols = new byte[length];
        for (var i = 0; i < length; i++)
        {
            symbols[i] = (byte)(((i * 3) + 1) % alphabet); // Never single-symbol.
        }

        RoundTripOnce(symbols, alphabet, 6, false, $"small-{length}");
    }

    [Fact]
    public void Fse_Encode_TooSmallInput_ReturnsMinusOne()
    {
        var ct = ZstdFseEncoder.BuildCTable(new short[] { 32, 32 }, 1, 6);
        var dst = new byte[64];
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, dst.Length, new byte[] { 1 }, 0, 1, ct));
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, dst.Length, new byte[] { 1, 2 }, 0, 2, ct));
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, 4, new byte[] { 1, 2, 3 }, 0, 3, ct));

        // RLE-shaped input and RLE (tableLog 0) tables also decline.
        var rle = "\t\t\t\t\t"u8.ToArray();
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, dst.Length, rle, 0, rle.Length, ct));
        var rleTable = new FseCTable { TableLog = 0, MaxSymbolValue = 9 };
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, dst.Length, new byte[] { 1, 2, 3 }, 0, 3, rleTable));

        // Tiny destination that fits the container check but not the stream.
        var many = MakeSymbols("random", 8, 200, 0xF5E);
        var (manyCounts, manyMaxSv) = Histogram(many, 8);
        var manyNorms = new short[8];
        Assert.Equal(6, ZstdFseEncoder.NormalizeCounts(manyNorms, manyCounts, many.Length, manyMaxSv, 6, false));
        var manyTable = ZstdFseEncoder.BuildCTable(manyNorms, manyMaxSv, 6);
        Assert.Equal(-1, ZstdFseEncoder.Encode(dst, 0, 16, many, 0, many.Length, manyTable));

        // Out-of-range symbols throw (caller bug, never a size decision).
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.Encode(dst, 0, dst.Length, new byte[] { 1, 2, 9 }, 0, 3, ct));
    }

    [Fact]
    public void Fse_TableLogSelection_MatchesReference()
    {
        Assert.Equal(7, ZstdFseEncoder.MinTableLogFor(3000, 35));
        Assert.Equal(6, ZstdFseEncoder.MinTableLogFor(3000, 28));
        Assert.Equal(9, ZstdFseEncoder.OptimalTableLog(9, 100000, 35));
        Assert.Equal(7, ZstdFseEncoder.OptimalTableLog(11, 100, 35));
        Assert.Equal(11, ZstdFseEncoder.OptimalTableLog(0, 100000, 35));
        Assert.Equal(7, ZstdFseEncoder.OptimalTableLog(6, 3000, 35));

        // Below the minimum the distribution is not representable.
        var (counts, maxSv) = Histogram(MakeSymbols("uniform", 36, 3000, 1), 36);
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.NormalizeCounts(new short[36], counts, 3000, maxSv, 6, false));
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.NormalizeCounts(new short[36], counts, 3000, maxSv, 4, false));
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.NormalizeCounts(new short[36], counts, 3000, maxSv, 13, false));
    }

    [Fact]
    public void Fse_WriteNCount_RejectsBadDistribution()
    {
        // Probabilities must sum to exactly 1 << tableLog.
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.WriteNCount(new byte[64], 0, 64, new short[] { 10, 10 }, 1, 6));
        Assert.Throws<ZstdException>(() =>
            ZstdFseEncoder.BuildCTable(new short[] { 10, 10 }, 1, 6));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void RoundTripOnce(byte[] symbols, int alphabet, int tableLog, bool useLowProb, string kind)
    {
        var (counts, maxSv) = Histogram(symbols, alphabet);
        var total = symbols.Length;
        var nonzero = 0;
        foreach (var c in counts)
        {
            if (c > 0)
            {
                nonzero++;
            }
        }

        var norms = new short[alphabet];
        if (tableLog < ZstdFseEncoder.MinTableLogFor(total, maxSv))
        {
            // Not representable at this accuracy (plan property, negative side).
            Assert.Throws<ZstdException>(() =>
                ZstdFseEncoder.NormalizeCounts(norms, counts, total, maxSv, tableLog, useLowProb));
            return;
        }

        var got = ZstdFseEncoder.NormalizeCounts(norms, counts, total, maxSv, tableLog, useLowProb);
        Assert.NotEqual(-1, got); // Test distributions are never RLE.
        Assert.Equal(tableLog, got);

        // Plan property: tableLog covers the nonzero-symbol count.
        Assert.True(tableLog >= CeilLog2(nonzero), $"{kind}: tableLog {tableLog} < ceil(log2({nonzero}))");

        // Present symbols keep a nonzero probability; the sum is exact.
        long sum = 0;
        for (var s = 0; s < alphabet; s++)
        {
            if (counts[s] > 0)
            {
                Assert.True(norms[s] != 0, $"{kind}: symbol {s} lost its probability");
            }

            sum += norms[s] == -1 ? 1 : norms[s];
        }

        Assert.Equal(1 << tableLog, sum);

        // Header round-trip through the existing decoder.
        var header = new byte[ZstdFseEncoder.NCountWriteBound(maxSv, tableLog)];
        var headerSize = ZstdFseEncoder.WriteNCount(header, 0, header.Length, norms, maxSv, tableLog);
        Assert.True(headerSize > 0 && headerSize <= header.Length);
        var consumed = ZstdFse.ParseNormalizedCounts(header, 0, headerSize, alphabet - 1,
            out var parsed, out var parsedLog, out var parsedMaxSv);
        Assert.Equal(headerSize, consumed);
        Assert.Equal(tableLog, parsedLog);
        for (var s = 0; s < alphabet; s++)
        {
            var expected = s <= parsedMaxSv ? parsed[s] : (short)0;
            Assert.True(norms[s] == expected, $"{kind}: norm mismatch at {s}");
        }

        for (var s = parsedMaxSv + 1; s < alphabet; s++)
        {
            Assert.Equal(0, norms[s]);
        }

        // Stream round-trip, decoded with the existing decoder tables.
        var ct = ZstdFseEncoder.BuildCTable(norms, maxSv, tableLog);
        var dst = new byte[ZstdFseEncoder.BlockBound(total)];
        var size = ZstdFseEncoder.Encode(dst, 0, dst.Length, symbols, 0, total, ct);
        Assert.True(size > 0, $"{kind}: encode failed");

        var dtable = ZstdFse.BuildTable(parsed, parsedMaxSv, parsedLog);
        var decoded = DecodeStream(dst, size, dtable, total);
        Assert.Equal(symbols, decoded);
    }

    /// <summary>
    /// Generic two-state FSE stream decoder (mirrors
    /// <c>FSE_decompress_usingDTable_generic</c> output order: state1, state2,
    /// alternating; each state's last symbol needs no state update since the
    /// encoder's <c>initCState2</c> symbols consume no bits). The exact-consumption
    /// assert proves the encoder wrote no missing or extra bits.
    /// </summary>
    private static byte[] DecodeStream(byte[] buf, int size, ZstdFse.DecodeTable table, int symbolCount)
    {
        var bitD = BackwardBitReader.ForSequenceStream(buf, 0, size);
        var log = table.TableLog;
        var s1 = (int)bitD.ReadBits(log);
        var s2 = (int)bitD.ReadBits(log);
        var output = new byte[symbolCount];
        for (var i = 0; i < symbolCount; i++)
        {
            if ((i & 1) == 0)
            {
                output[i] = (byte)table.Symbols[s1];
                if (i + 2 < symbolCount)
                {
                    s1 = table.NewState[s1] + (int)bitD.ReadBits(table.NumBits[s1]);
                }
            }
            else
            {
                output[i] = (byte)table.Symbols[s2];
                if (i + 2 < symbolCount)
                {
                    s2 = table.NewState[s2] + (int)bitD.ReadBits(table.NumBits[s2]);
                }
            }
        }

        Assert.True(bitD.IsAtEnd, "FSE stream not exactly consumed");
        return output;
    }

    private static (uint[] Counts, int MaxSymbolValue) Histogram(byte[] symbols, int alphabet)
    {
        var counts = new uint[alphabet];
        var maxSv = 0;
        foreach (var s in symbols)
        {
            counts[s]++;
            if (s > maxSv)
            {
                maxSv = s;
            }
        }

        return (counts, maxSv);
    }

    private static byte[] MakeSymbols(string kind, int alphabet, int count, int seed)
    {
        var rnd = new Random(seed);
        var symbols = new byte[count];
        switch (kind)
        {
            case "skewed": // One hot: 70% symbol 0, rest spread.
                for (var i = 0; i < count; i++)
                {
                    symbols[i] = rnd.NextDouble() < 0.7 ? (byte)0 : (byte)rnd.Next(1, alphabet);
                }

                break;
            case "uniform":
                for (var i = 0; i < count; i++)
                {
                    symbols[i] = (byte)rnd.Next(0, alphabet);
                }

                break;
            default: // "random": Dirichlet-ish random weights (varied skew).
                var weights = new double[alphabet];
                double wsum = 0;
                for (var s = 0; s < alphabet; s++)
                {
                    weights[s] = 0.05 + Math.Pow(rnd.NextDouble(), 3);
                    wsum += weights[s];
                }

                var cumul = new double[alphabet];
                double acc = 0;
                for (var s = 0; s < alphabet; s++)
                {
                    acc += weights[s] / wsum;
                    cumul[s] = acc;
                }

                for (var i = 0; i < count; i++)
                {
                    var v = rnd.NextDouble();
                    var s = 0;
                    while (s < alphabet - 1 && cumul[s] < v)
                    {
                        s++;
                    }

                    symbols[i] = (byte)s;
                }

                break;
        }

        return symbols;
    }

    private static int SeedFor(int alphabet, int tableLog, bool useLowProb, int dist)
    {
        return (alphabet * 100003) + (tableLog * 1013) + (useLowProb ? 77 : 0) + (dist * 37) + 11;
    }

    private static int CeilLog2(int v)
    {
        if (v <= 1)
        {
            return 0;
        }

        return 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v - 1);
    }
}