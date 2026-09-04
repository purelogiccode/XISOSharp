#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace ZARSharp.Zstd;

/// <summary>
/// Match-finder parameters for one level, from the
/// <c>srcSize &lt;= 128 KiB</c> row of <c>lib/compress/clevels.h</c>
/// (columns W C H S L T = windowLog chainLog hashLog searchLog minMatch
/// targetLength). Informational view only (tests, diagnostics); the live
/// search resolves the size-tier row per input (see
/// <see cref="ZstdCompressionParameters.ForSizeAndLevel"/>).
/// </summary>
/// <param name="WindowLog">Window log (single-shot window covers the block).</param>
/// <param name="ChainLog">Hash-chain log (lazy) / small-hash log (double-fast).</param>
/// <param name="HashLog">Hash table log.</param>
/// <param name="SearchLog">Chain search depth log (lazy only).</param>
/// <param name="MinMatch">Minimum match length used for hashing.</param>
/// <param name="TargetLength">Target match length (fast step size).</param>
/// <param name="Depth">Lazy depth: 0 = greedy, 1 = lazy, 2 = lazy2/btlazy2.</param>
/// <param name="UseChain">False = fast (hash table only), true = lazy/greedy.</param>
public readonly record struct ZstdMatchParams(
    int WindowLog,
    int ChainLog,
    int HashLog,
    int SearchLog,
    int MinMatch,
    int TargetLength,
    int Depth,
    bool UseChain)
{
    /// <summary>Parameters for <paramref name="level"/> (1..22, Le128K row).</summary>
    public static ZstdMatchParams ForLevel(int level)
    {
        // True Le128K row verbatim (see ZstdCompressionParameters). The live
        // search resolves the size-tier row per input instead.
        var p = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Le128K, level);
        var useChain = p.Strategy is not ZstdStrategy.Fast and not ZstdStrategy.DoubleFast;
        var depth = p.Strategy switch
        {
            ZstdStrategy.Greedy => 0,
            ZstdStrategy.Lazy => 1,
            ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2 => 2,
            _ => 1,
        };
        return new ZstdMatchParams(
            p.WindowLog, p.ChainLog, p.HashLog, p.SearchLog,
            p.MinMatch, p.TargetLength, depth, useChain);
    }
}

/// <summary>
/// zstd match finder for single-shot blocks (no dictionaries, window starts
/// empty, all matches bounded by the current block). Exact ports, dispatched
/// by the size-tier strategy row (see
/// <see cref="ZstdCompressionParameters.ForSizeAndLevel"/>):
/// <list type="number">
/// <item><b>Fast</b>: <see cref="ZstdFast"/> (<c>lib/compress/zstd_fast.c</c>
/// <c>ZSTD_compressBlock_fast_noDict_generic</c>).</item>
/// <item><b>Double-fast</b>: <see cref="ZstdDoubleFast"/>
/// (<c>lib/compress/zstd_double_fast.c</c>, long + small tables).</item>
/// <item><b>Greedy / lazy / lazy2</b>: <see cref="ZstdLazyEngine"/>
/// (<c>lib/compress/zstd_lazy.c</c> depth 0/1/2, row hash when the adjusted
/// windowLog &gt; 14, hash chain otherwise).</item>
/// <item><b>Binary-tree lazy2</b>: <see cref="ZstdLazyEngine"/> depth 2 over
/// <see cref="ZstdBinaryTree"/> (<c>ZSTD_compressBlock_btlazy2</c>).</item>
/// <item><b>Optimal parsing</b>: <see cref="ZstdOpt"/>
/// (<c>ZSTD_compressBlock_btopt</c> / <c>btultra</c> / <c>btultra2</c>).</item>
/// </list>
/// <para/>
/// Deliberate guards (behavior-neutral with probability 1 − 2⁻³²):
/// <c>ZSTD_hashPtr</c> reads are zero-padded at the tail (upstream over-reads
/// up to 7 bytes past the search limit — safe in C, an exception in C#) and
/// out-of-range lookbehinds are treated as mismatches. Padded hashes only
/// affect which matches are <em>found</em>; every candidate is still verified
/// with in-bounds compares and a bounded count, so output stays valid.
/// </summary>
public sealed class ZstdMatchFinder
{
    // Prime constants from lib/compress/zstd_compress_internal.h:898-926.
    private const uint Prime4Bytes = 2654435761U;
    private const ulong Prime5Bytes = 889523592379UL;
    private const ulong Prime6Bytes = 227718039650203UL;
    private const ulong Prime7Bytes = 58295818150454627UL;
    private const ulong Prime8Bytes = 0xCF1BBCDCB7A56463UL;

    /// <summary>Creates a finder for <paramref name="level"/> (1..22).</summary>
    public ZstdMatchFinder(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        Params = ZstdMatchParams.ForLevel(level);
        Level = level;
    }

    /// <summary>Compression level (1..22).</summary>
    public int Level { get; }

    /// <summary>Effective strategy (Le128K row; live search uses the size-tier row).</summary>
    public ZstdStrategy Strategy => ZstdCompressionParameters.ForTierLevel(
        ZstdCompressionParameters.SizeTier.Le128K, Level).Strategy;

    /// <summary>Parameters in effect (Le128K informational view).</summary>
    public ZstdMatchParams Params { get; }

    /// <summary>
    /// Parses <paramref name="source"/> into <paramref name="store"/> (sequences
    /// plus trailing literals) and updates the 3-entry
    /// <paramref name="repeatOffsets"/> history in place (initialize to
    /// <c>{1,4,8}</c> per fresh frame). Returns the trailing literal length.
    /// Mirrors the <c>ZSTD_compressBlock_*</c> contract (sequences stored,
    /// <c>lastLits</c> returned, <c>rep</c> saved for the next block).
    /// </summary>
    public int FindMatches(ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repeatOffsets);
        if (repeatOffsets.Length < ZstdSeq.RepNum)
        {
            throw new ArgumentException("Repeat history needs 3 entries.", nameof(repeatOffsets));
        }

        if (source.Length == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        // Tier row + ZSTD_adjustCParams_internal (no dict), exactly like the
        // frame header (ZstdCompressor.WriteFrameHeader).
        var prm = ZstdCompressionParameters.ForSizeAndLevel(source.Length, Level).AdjustForSize(source.Length);
        return prm.Strategy switch
        {
            ZstdStrategy.Fast => ZstdFast.FindMatches(source, store, repeatOffsets, prm),
            ZstdStrategy.DoubleFast => ZstdDoubleFast.FindMatches(source, store, repeatOffsets, prm),
            ZstdStrategy.Greedy or ZstdStrategy.Lazy or ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2 =>
                ZstdLazyEngine.FindMatches(source, store, repeatOffsets, Level),
            ZstdStrategy.BtOpt or ZstdStrategy.BtUltra or ZstdStrategy.BtUltra2 =>
                ZstdOpt.FindMatches(source, store, repeatOffsets, Level),
            _ => throw new NotSupportedException($"Unknown strategy {prm.Strategy}."),
        };
    }

    // ------------------------------------------------------------------
    // Hashing (ZSTD_hashPtr)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ZSTD_hashPtr(p, hBits, mls)</c>. Reads up to 8 bytes little-endian at
    /// <paramref name="pos"/>, zero-padded past the end (see class remarks).
    /// </summary>
    internal static uint HashPtr(ReadOnlySpan<byte> src, int pos, int hashLog, int minMatch)
    {
        var value = Read64Padded(src, pos);
        if (minMatch <= 4)
        {
            return (uint)(((uint)value * Prime4Bytes) >> (32 - hashLog));
        }

        if (minMatch == 5)
        {
            return (uint)(((value << 24) * Prime5Bytes) >> (64 - hashLog));
        }

        if (minMatch == 6)
        {
            return (uint)(((value << 16) * Prime6Bytes) >> (64 - hashLog));
        }

        if (minMatch == 7)
        {
            return (uint)(((value << 8) * Prime7Bytes) >> (64 - hashLog));
        }

        return (uint)((value * Prime8Bytes) >> (64 - hashLog));
    }

    private static ulong Read64Padded(ReadOnlySpan<byte> src, int pos)
    {
        ulong value = 0;
        var available = src.Length - pos;
        if (available > 8)
        {
            available = 8;
        }

        for (var i = 0; i < available; i++)
        {
            value |= (ulong)src[pos + i] << (8 * i);
        }

        return value;
    }
}
