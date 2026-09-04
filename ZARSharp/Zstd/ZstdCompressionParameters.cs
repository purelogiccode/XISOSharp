namespace ZARSharp.Zstd;

using System.Numerics;
using System.Runtime.InteropServices;

// PortPlan Step 1: full port of lib/compress/clevels.h
// (zstd-1.5.7). Four source-size tiers, levels 0..22 (0 = base row for
// negative levels). Columns: W C H S L T = windowLog chainLog hashLog
// searchLog minMatch targetLength + strategy.
//
// Tier selection in libzstd (ZSTD_getCParams):
//   srcSize > 256 KiB  -> row 0 ("default")
//   srcSize <= 256 KiB -> row 1
//   srcSize <= 128 KiB -> row 2  (ZAR blocks are always 64 KiB: this row applies)
//   srcSize <= 16 KiB  -> row 3
// Single-shot ZAR note: the frame content size is 65536, so row 2 is the
// only row the block encoder consults today. The other rows are ported
// verbatim and honored per input size by the match finders.

/// <summary>
/// Exact C# port of <c>ZSTD_defaultCParameters</c> from
/// <c>lib/compress/clevels.h</c> (zstd-1.5.7). No values invented.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ZstdCompressionParameters(
    int WindowLog,
    int ChainLog,
    int HashLog,
    int SearchLog,
    int MinMatch,
    int TargetLength,
    ZstdStrategy Strategy)
{
    /// <summary>Size-tier selector matching ZSTD_getCParams thresholds.</summary>
    public enum SizeTier
    {
        /// <summary>Any srcSize &gt; 256 KiB.</summary>
        Default = 0,

        /// <summary>srcSize &lt;= 256 KiB.</summary>
        Le256K = 1,

        /// <summary>srcSize &lt;= 128 KiB (ZAR 64 KiB blocks land here).</summary>
        Le128K = 2,

        /// <summary>srcSize &lt;= 16 KiB.</summary>
        Le16K = 3,
    }

    private static readonly ZstdCompressionParameters[,,] Table = BuildTable();

    /// <summary>Looks up parameters for a tier and level (0..22).</summary>
    public static ZstdCompressionParameters ForTierLevel(SizeTier tier, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        return Table[(int)tier, level, 0];
    }

    /// <summary>
    /// Selects the tier for <paramref name="srcSize"/> exactly like
    /// <c>ZSTD_getCParams</c>, then returns the row for
    /// <paramref name="level"/> (1..22).
    /// </summary>
    public static ZstdCompressionParameters ForSizeAndLevel(long srcSize, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        return ForTierLevel(TierForSize(srcSize), level);
    }

    /// <summary>Tier selection matching <c>ZSTD_getCParams</c> thresholds.</summary>
    public static SizeTier TierForSize(long srcSize)
    {
        if (srcSize <= 16 * 1024)
        {
            return SizeTier.Le16K;
        }

        if (srcSize <= 128 * 1024)
        {
            return SizeTier.Le128K;
        }

        if (srcSize <= 256 * 1024)
        {
            return SizeTier.Le256K;
        }

        return SizeTier.Default;
    }

    private const int HashLogMin = 6; // ZSTD_HASHLOG_MIN (lib/zstd.h).
    private const int WindowLogAbsoluteMin = 10; // ZSTD_WINDOWLOG_ABSOLUTEMIN.

    /// <summary>
    /// Exact port of <c>ZSTD_adjustCParams_internal</c>
    /// (<c>lib/compress/zstd_compress.c</c>) for the no-dictionary,
    /// unknown-mode single-shot case: downsizes <see cref="WindowLog"/>,
    /// <see cref="HashLog"/> and <see cref="ChainLog"/> for small
    /// <paramref name="srcSize"/> (less memory + faster init upstream;
    /// different tables upstream, so required for byte parity).
    /// Omitted branches can never trigger here, by construction:
    /// dictionary branches (<c>dictSize == 0</c>), the row-finder hash cap
    /// (needs <c>hashLog &gt; 24 + rowLog</c>; table max is 25 with rowLog ≥ 4
    /// only at levels whose hashLog ≤ 19), and the CDict short-cache cap.
    /// </summary>
    public ZstdCompressionParameters AdjustForSize(long srcSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(srcSize);
        var windowLog = WindowLog;
        var chainLog = ChainLog;
        var hashLog = HashLog;

        // Resize windowLog if input is small enough (maxWindowResize = 1 << 30).
        if (srcSize <= 1L << 30)
        {
            var tSize = (ulong)srcSize;
            var srcLog = tSize < (1UL << HashLogMin)
                ? (uint)HashLogMin
                : (uint)(31 - BitOperations.LeadingZeroCount((uint)(tSize - 1))) + 1;
            if (windowLog > (int)srcLog)
            {
                windowLog = (int)srcLog;
            }
        }

        // No dictionary: dictAndWindowLog == windowLog.
        var cycleLog = chainLog - (Strategy >= ZstdStrategy.BtLazy2 ? 1 : 0);
        if (hashLog > windowLog + 1)
        {
            hashLog = windowLog + 1;
        }

        if (cycleLog > windowLog)
        {
            chainLog -= cycleLog - windowLog;
        }

        // Minimum window log required for a valid frame header.
        if (windowLog < WindowLogAbsoluteMin)
        {
            windowLog = WindowLogAbsoluteMin;
        }

        return this with { WindowLog = windowLog, ChainLog = chainLog, HashLog = hashLog };
    }

    // Table rows copied verbatim from clevels.h (W C H S L T strat).
    private static ZstdCompressionParameters[,,] BuildTable()
    {
        var t = new ZstdCompressionParameters[4, 23, 1];

        // Row 0: default, srcSize > 256 KB.
        t[0, 0, 0] = new(19, 12, 13, 1, 6, 1, ZstdStrategy.Fast);
        t[0, 1, 0] = new(19, 13, 14, 1, 7, 0, ZstdStrategy.Fast);
        t[0, 2, 0] = new(20, 15, 16, 1, 6, 0, ZstdStrategy.Fast);
        t[0, 3, 0] = new(21, 16, 17, 1, 5, 0, ZstdStrategy.DoubleFast);
        t[0, 4, 0] = new(21, 18, 18, 1, 5, 0, ZstdStrategy.DoubleFast);
        t[0, 5, 0] = new(21, 18, 19, 3, 5, 2, ZstdStrategy.Greedy);
        t[0, 6, 0] = new(21, 18, 19, 3, 5, 4, ZstdStrategy.Lazy);
        t[0, 7, 0] = new(21, 19, 20, 4, 5, 8, ZstdStrategy.Lazy);
        t[0, 8, 0] = new(21, 19, 20, 4, 5, 16, ZstdStrategy.Lazy2);
        t[0, 9, 0] = new(22, 20, 21, 4, 5, 16, ZstdStrategy.Lazy2);
        t[0, 10, 0] = new(22, 21, 22, 5, 5, 16, ZstdStrategy.Lazy2);
        t[0, 11, 0] = new(22, 21, 22, 6, 5, 16, ZstdStrategy.Lazy2);
        t[0, 12, 0] = new(22, 22, 23, 6, 5, 32, ZstdStrategy.Lazy2);
        t[0, 13, 0] = new(22, 22, 22, 4, 5, 32, ZstdStrategy.BtLazy2);
        t[0, 14, 0] = new(22, 22, 23, 5, 5, 32, ZstdStrategy.BtLazy2);
        t[0, 15, 0] = new(22, 23, 23, 6, 5, 32, ZstdStrategy.BtLazy2);
        t[0, 16, 0] = new(22, 22, 22, 5, 5, 48, ZstdStrategy.BtOpt);
        t[0, 17, 0] = new(23, 23, 22, 5, 4, 64, ZstdStrategy.BtOpt);
        t[0, 18, 0] = new(23, 23, 22, 6, 3, 64, ZstdStrategy.BtUltra);
        t[0, 19, 0] = new(23, 24, 22, 7, 3, 256, ZstdStrategy.BtUltra2);
        t[0, 20, 0] = new(25, 25, 23, 7, 3, 256, ZstdStrategy.BtUltra2);
        t[0, 21, 0] = new(26, 26, 24, 7, 3, 512, ZstdStrategy.BtUltra2);
        t[0, 22, 0] = new(27, 27, 25, 9, 3, 999, ZstdStrategy.BtUltra2);

        // Row 1: srcSize <= 256 KB.
        t[1, 0, 0] = new(18, 12, 13, 1, 5, 1, ZstdStrategy.Fast);
        t[1, 1, 0] = new(18, 13, 14, 1, 6, 0, ZstdStrategy.Fast);
        t[1, 2, 0] = new(18, 14, 14, 1, 5, 0, ZstdStrategy.DoubleFast);
        t[1, 3, 0] = new(18, 16, 16, 1, 4, 0, ZstdStrategy.DoubleFast);
        t[1, 4, 0] = new(18, 16, 17, 3, 5, 2, ZstdStrategy.Greedy);
        t[1, 5, 0] = new(18, 17, 18, 5, 5, 2, ZstdStrategy.Greedy);
        t[1, 6, 0] = new(18, 18, 19, 3, 5, 4, ZstdStrategy.Lazy);
        t[1, 7, 0] = new(18, 18, 19, 4, 4, 4, ZstdStrategy.Lazy);
        t[1, 8, 0] = new(18, 18, 19, 4, 4, 8, ZstdStrategy.Lazy2);
        t[1, 9, 0] = new(18, 18, 19, 5, 4, 8, ZstdStrategy.Lazy2);
        t[1, 10, 0] = new(18, 18, 19, 6, 4, 8, ZstdStrategy.Lazy2);
        t[1, 11, 0] = new(18, 18, 19, 5, 4, 12, ZstdStrategy.BtLazy2);
        t[1, 12, 0] = new(18, 19, 19, 7, 4, 12, ZstdStrategy.BtLazy2);
        t[1, 13, 0] = new(18, 18, 19, 4, 4, 16, ZstdStrategy.BtOpt);
        t[1, 14, 0] = new(18, 18, 19, 4, 3, 32, ZstdStrategy.BtOpt);
        t[1, 15, 0] = new(18, 18, 19, 6, 3, 128, ZstdStrategy.BtOpt);
        t[1, 16, 0] = new(18, 19, 19, 6, 3, 128, ZstdStrategy.BtUltra);
        t[1, 17, 0] = new(18, 19, 19, 8, 3, 256, ZstdStrategy.BtUltra);
        t[1, 18, 0] = new(18, 19, 19, 6, 3, 128, ZstdStrategy.BtUltra2);
        t[1, 19, 0] = new(18, 19, 19, 8, 3, 256, ZstdStrategy.BtUltra2);
        t[1, 20, 0] = new(18, 19, 19, 10, 3, 512, ZstdStrategy.BtUltra2);
        t[1, 21, 0] = new(18, 19, 19, 12, 3, 512, ZstdStrategy.BtUltra2);
        t[1, 22, 0] = new(18, 19, 19, 13, 3, 999, ZstdStrategy.BtUltra2);

        // Row 2: srcSize <= 128 KB (ZAR 64 KiB blocks).
        t[2, 0, 0] = new(17, 12, 12, 1, 5, 1, ZstdStrategy.Fast);
        t[2, 1, 0] = new(17, 12, 13, 1, 6, 0, ZstdStrategy.Fast);
        t[2, 2, 0] = new(17, 13, 15, 1, 5, 0, ZstdStrategy.Fast);
        t[2, 3, 0] = new(17, 15, 16, 2, 5, 0, ZstdStrategy.DoubleFast);
        t[2, 4, 0] = new(17, 17, 17, 2, 4, 0, ZstdStrategy.DoubleFast);
        t[2, 5, 0] = new(17, 16, 17, 3, 4, 2, ZstdStrategy.Greedy);
        t[2, 6, 0] = new(17, 16, 17, 3, 4, 4, ZstdStrategy.Lazy);
        t[2, 7, 0] = new(17, 16, 17, 3, 4, 8, ZstdStrategy.Lazy2);
        t[2, 8, 0] = new(17, 16, 17, 4, 4, 8, ZstdStrategy.Lazy2);
        t[2, 9, 0] = new(17, 16, 17, 5, 4, 8, ZstdStrategy.Lazy2);
        t[2, 10, 0] = new(17, 16, 17, 6, 4, 8, ZstdStrategy.Lazy2);
        t[2, 11, 0] = new(17, 17, 17, 5, 4, 8, ZstdStrategy.BtLazy2);
        t[2, 12, 0] = new(17, 18, 17, 7, 4, 12, ZstdStrategy.BtLazy2);
        t[2, 13, 0] = new(17, 18, 17, 3, 4, 12, ZstdStrategy.BtOpt);
        t[2, 14, 0] = new(17, 18, 17, 4, 3, 32, ZstdStrategy.BtOpt);
        t[2, 15, 0] = new(17, 18, 17, 6, 3, 256, ZstdStrategy.BtOpt);
        t[2, 16, 0] = new(17, 18, 17, 6, 3, 128, ZstdStrategy.BtUltra);
        t[2, 17, 0] = new(17, 18, 17, 8, 3, 256, ZstdStrategy.BtUltra);
        t[2, 18, 0] = new(17, 18, 17, 10, 3, 512, ZstdStrategy.BtUltra);
        t[2, 19, 0] = new(17, 18, 17, 5, 3, 256, ZstdStrategy.BtUltra2);
        t[2, 20, 0] = new(17, 18, 17, 7, 3, 512, ZstdStrategy.BtUltra2);
        t[2, 21, 0] = new(17, 18, 17, 9, 3, 512, ZstdStrategy.BtUltra2);
        t[2, 22, 0] = new(17, 18, 17, 11, 3, 999, ZstdStrategy.BtUltra2);

        // Row 3: srcSize <= 16 KB.
        t[3, 0, 0] = new(14, 12, 13, 1, 5, 1, ZstdStrategy.Fast);
        t[3, 1, 0] = new(14, 14, 15, 1, 5, 0, ZstdStrategy.Fast);
        t[3, 2, 0] = new(14, 14, 15, 1, 4, 0, ZstdStrategy.Fast);
        t[3, 3, 0] = new(14, 14, 15, 2, 4, 0, ZstdStrategy.DoubleFast);
        t[3, 4, 0] = new(14, 14, 14, 4, 4, 2, ZstdStrategy.Greedy);
        t[3, 5, 0] = new(14, 14, 14, 3, 4, 4, ZstdStrategy.Lazy);
        t[3, 6, 0] = new(14, 14, 14, 4, 4, 8, ZstdStrategy.Lazy2);
        t[3, 7, 0] = new(14, 14, 14, 6, 4, 8, ZstdStrategy.Lazy2);
        t[3, 8, 0] = new(14, 14, 14, 8, 4, 8, ZstdStrategy.Lazy2);
        t[3, 9, 0] = new(14, 15, 14, 5, 4, 8, ZstdStrategy.BtLazy2);
        t[3, 10, 0] = new(14, 15, 14, 9, 4, 8, ZstdStrategy.BtLazy2);
        t[3, 11, 0] = new(14, 15, 14, 3, 4, 12, ZstdStrategy.BtOpt);
        t[3, 12, 0] = new(14, 15, 14, 4, 3, 24, ZstdStrategy.BtOpt);
        t[3, 13, 0] = new(14, 15, 14, 5, 3, 32, ZstdStrategy.BtUltra);
        t[3, 14, 0] = new(14, 15, 15, 6, 3, 64, ZstdStrategy.BtUltra);
        t[3, 15, 0] = new(14, 15, 15, 7, 3, 256, ZstdStrategy.BtUltra);
        t[3, 16, 0] = new(14, 15, 15, 5, 3, 48, ZstdStrategy.BtUltra2);
        t[3, 17, 0] = new(14, 15, 15, 6, 3, 128, ZstdStrategy.BtUltra2);
        t[3, 18, 0] = new(14, 15, 15, 7, 3, 256, ZstdStrategy.BtUltra2);
        t[3, 19, 0] = new(14, 15, 15, 8, 3, 256, ZstdStrategy.BtUltra2);
        t[3, 20, 0] = new(14, 15, 15, 8, 3, 512, ZstdStrategy.BtUltra2);
        t[3, 21, 0] = new(14, 15, 15, 9, 3, 512, ZstdStrategy.BtUltra2);
        t[3, 22, 0] = new(14, 15, 15, 10, 3, 999, ZstdStrategy.BtUltra2);

        return t;
    }
}
