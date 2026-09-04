using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 1 acceptance: full <c>clevels.h</c> table, levels 1..22 API,
/// and container parity foundations (deterministic raw-block bytes).
/// </summary>
public sealed class Step1ParityTests
{
    [Fact]
    public void CLevels_Le128KRow_SpotChecks()
    {
        // Verbatim from lib/compress/clevels.h row "<= 128 KiB".
        var l1 = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Le128K, 1);
        Assert.Equal((17, 12, 13, 1, 6, 0, ZstdStrategy.Fast),
            (l1.WindowLog, l1.ChainLog, l1.HashLog, l1.SearchLog, l1.MinMatch, l1.TargetLength, l1.Strategy));

        var l6 = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Le128K, 6);
        Assert.Equal((17, 16, 17, 3, 4, 4, ZstdStrategy.Lazy),
            (l6.WindowLog, l6.ChainLog, l6.HashLog, l6.SearchLog, l6.MinMatch, l6.TargetLength, l6.Strategy));

        var l22 = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Le128K, 22);
        Assert.Equal((17, 18, 17, 11, 3, 999, ZstdStrategy.BtUltra2),
            (l22.WindowLog, l22.ChainLog, l22.HashLog, l22.SearchLog, l22.MinMatch, l22.TargetLength, l22.Strategy));
    }

    [Fact]
    public void CLevels_TierSelection_MatchesGetCParams()
    {
        Assert.Equal(ZstdCompressionParameters.SizeTier.Le16K, ZstdCompressionParameters.TierForSize(16 * 1024));
        Assert.Equal(ZstdCompressionParameters.SizeTier.Le128K, ZstdCompressionParameters.TierForSize(64 * 1024));
        Assert.Equal(ZstdCompressionParameters.SizeTier.Le256K, ZstdCompressionParameters.TierForSize(256 * 1024));
        Assert.Equal(ZstdCompressionParameters.SizeTier.Default, ZstdCompressionParameters.TierForSize((256 * 1024) + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(22)]
    public void FromLevel_Accepts1Through22(int level)
    {
        Assert.Equal(level, ZstdCompressionOptions.FromLevel(level).Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void FromLevel_RejectsOutside1Through22(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdCompressionOptions.FromLevel(level));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void MatchFinder_FastThroughLazy2_Supported(int level)
    {
        // Step 3a: fast/double-fast/greedy/lazy/lazy2 resolve and parse.
        // Small input: Le16K tier, levels 7-8 are still lazy2 there.
        var finder = new ZstdMatchFinder(level);
        var store = new ZstdSequenceStore(16);
        var rep = ZstdSeq.FreshRepeatOffsets();
        finder.FindMatches(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, store, rep);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void MatchFinder_Lazy2MidTiers_Supported(int level)
    {
        // 64 KiB input: Le128K tier, levels 9-10 are lazy2 there
        // (at the Le16K tier they already resolve to btlazy2 — staged).
        var finder = new ZstdMatchFinder(level);
        var store = new ZstdSequenceStore(65536);
        var rep = ZstdSeq.FreshRepeatOffsets();
        finder.FindMatches(new byte[65536], store, rep);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(22)]
    public void MatchFinder_AllStrategies_Supported(int level)
    {
        // Step 3b: binary-tree and optimal-parsing strategies resolve and parse.
        var finder = new ZstdMatchFinder(level);
        var store = new ZstdSequenceStore(65536);
        var rep = ZstdSeq.FreshRepeatOffsets();
        finder.FindMatches(new byte[65536], store, rep);
    }

    [Fact]
    public void Container_RawBlocks_SameSequenceYieldsIdenticalBytes()
    {
        static byte[] Build()
        {
            using var ms = new MemoryStream();
            using (var w = new ZArchiveWriter(ms, new ZarRawCompressor()))
            {
                Assert.True(w.MakeDir("docs"));
                Assert.True(w.StartNewFile("docs/a.txt"));
                w.AppendData([1, 2, 3, 4]);
                Assert.True(w.StartNewFile("b.bin"));
                w.AppendData(new byte[70000]);
                w.Finalize();
            }

            return ms.ToArray();
        }

        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void Tool_Pack_DeterministicFlag_RoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "zarsharp", "step1_" + Guid.NewGuid().ToString("N"));
        var indir = Path.Combine(tmp, "in");
        Directory.CreateDirectory(Path.Combine(indir, "sub"));
        File.WriteAllBytes(Path.Combine(indir, "x.txt"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(indir, "sub", "y.bin"), [4, 5]);
        try
        {
            var sorted = Path.Combine(tmp, "s.zar");
            ZArchiveTool.Pack(indir, sorted, deterministicOrder: true);
            var native = Path.Combine(tmp, "n.zar");
            ZArchiveTool.Pack(indir, native, deterministicOrder: false);

            // Both must extract to identical content (order may differ).
            ZArchiveTool.Extract(sorted, Path.Combine(tmp, "o1"));
            ZArchiveTool.Extract(native, Path.Combine(tmp, "o2"));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(tmp, "o1", "x.txt")),
                File.ReadAllBytes(Path.Combine(tmp, "o2", "x.txt")));
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
