using System.Numerics;
using System.Runtime.InteropServices;

namespace ZARSharp.Zstd;

/// <summary>
/// Exact C# port of the optimal parser — <c>ZSTD_compressBlock_opt_generic</c>
/// (optLevel 0 for <c>btopt</c>, 2 for <c>btultra</c>/<c>btultra2</c>),
/// <c>ZSTD_insertBtAndGetAllMatches</c>, <c>ZSTD_insertBt1</c>,
/// <c>ZSTD_updateTree_internal</c> and the price machinery
/// (<c>lib/compress/zstd_opt.c</c>, zstd-1.5.7), <c>ZSTD_noDict</c> subset
/// (dictionary and LDM branches deleted; LDM never triggers single-shot with
/// windowLog ≤ 16). Covers the <see cref="ZstdStrategy.BtOpt"/>,
/// <see cref="ZstdStrategy.BtUltra"/> and
/// <see cref="ZstdStrategy.BtUltra2"/> tier rows. Parameters come from the
/// size-tier row plus <see cref="ZstdCompressionParameters.AdjustForSize"/>.
/// <para/>
/// Faithful details: predefined prices for inputs ≤ 8 bytes, frequency init
/// from the first block (<c>HIST_count_simple</c> + downscale + baseline
/// tables), static (optLevel 0) vs accurate (optLevel 2) weights, the 3-byte
/// hash table when minMatch is 3, repcode-first match enumeration with
/// <c>sufficient_len</c> early return, the forward price pass with the
/// optLevel-0 position skip and early update abort, match+1-literal lookahead,
/// reverse traversal emitting stretches as sequences with per-series stats
/// update, and the <c>btultra2</c> two-pass stats seeding (a throwaway pass
/// followed by a real pass over fresh tables with seeded statistics, which is
/// exactly what the native window-shift invalidation amounts to).
/// <para/>
/// Index discipline: table slots store <c>pos + 1</c> (never bare <c>pos</c>),
/// mirroring upstream where absolute indices start at
/// <c>ZSTD_WINDOW_START_INDEX (2)</c> and slot value 0 means "empty".
/// Deliberate guards: hash reads are zero-padded at the tail (upstream
/// over-reads there); every candidate is verified in bounds. End-of-block
/// compare hazards need no guard (the first candidate reaching the end
/// strictly improves and breaks first). One native construction cannot be
/// reproduced: when the previous optimal entry is a fill marker
/// (<c>litlen == !0</c>), the literal-take price reads out of bounds upstream
/// (release-build garbage); the port declines the take there, which matches
/// observed native output on the full parity corpus including adversarial
/// hybrids (long-match runs abutting incompressible tails).
/// </summary>
internal static class ZstdOpt
{
    internal const int OptNum = 4096;
    private const int OptSize = OptNum + 3;
    private const int MaxPrice = 1 << 30;
    private const uint BitcostMultiplier = 1u << 8;
    private const uint LitfreqAdd = 2;
    private const int PredefThreshold = 8;
    private const int HashLog3Max = 17;
    private const uint FillLitlen = 0xFFFFFFFFu; // opt[].litlen "not an end of match"
    private const uint Prime3Bytes = 506832829U;

    private static readonly uint[] BaseLitLengthFreqs =
    [
        4, 2, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1,
    ];

    private static readonly uint[] BaseOffCodeFreqs =
    [
        6, 2, 1, 1, 2, 3, 4, 4,
        4, 3, 2, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>One candidate match (offBase + raw length).</summary>
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct OptMatch(uint Off, uint Len);

    /// <summary>One optimal-parser position (<c>ZSTD_optimal_t</c>).</summary>
    [StructLayout(LayoutKind.Auto)]
    internal struct Optimal
    {
        internal int Price;
        internal uint Off;
        internal uint Mlen;
        internal uint Litlen;
        internal uint Rep0;
        internal uint Rep1;
        internal uint Rep2;
    }

    /// <summary>Adaptive price statistics (<c>optState_t</c> frequency half).</summary>
    internal sealed class OptStats
    {
        internal readonly uint[] LitFreq = new uint[256];
        internal readonly uint[] LitLengthFreq = new uint[36];
        internal readonly uint[] MatchLengthFreq = new uint[53];
        internal readonly uint[] OffCodeFreq = new uint[32];
        internal uint LitSum;
        internal uint LitLengthSum;
        internal uint MatchLengthSum;
        internal uint OffCodeSum;
        internal uint LitSumBasePrice;
        internal uint LitLengthSumBasePrice;
        internal uint MatchLengthSumBasePrice;
        internal uint OffCodeSumBasePrice;
        internal bool Predef;
    }

    private static int Highbit32(uint value)
    {
        return 31 - BitOperations.LeadingZeroCount(value);
    }

    private static int WindowLow(int curr, int windowLog)
    {
        var window = 1 << windowLog;
        return curr > window ? curr - window : 0;
    }

    /// <summary>Converts a raw table slot to a position (−1 = empty).</summary>
    private static int SlotPos(uint raw)
    {
        return (int)raw - 1;
    }

    private static uint Read32LE(ReadOnlySpan<byte> src, int pos)
    {
        uint value = 0;
        for (var i = 0; i < 4 && pos + i < src.Length; i++)
        {
            value |= (uint)src[pos + i] << (8 * i);
        }

        return value;
    }

    private static int CountMatches(ReadOnlySpan<byte> src, int ip, int match, int end)
    {
        var start = ip;
        while (ip < end && src[ip] == src[match])
        {
            ip++;
            match++;
        }

        return ip - start;
    }

    /// <summary>
    /// Parses <paramref name="source"/> exactly like native btopt/btultra/
    /// btultra2 at <paramref name="level"/> and stores sequences into
    /// <paramref name="store"/>. <paramref name="repeatOffsets"/> is the
    /// frame-scoped history (init <c>{1,4,8}</c>), evolved per the native
    /// shortest-path rule. Returns the trailing literal length.
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets, int level)
    {
        if (source.Length == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        var table = ZstdCompressionParameters.ForSizeAndLevel(source.Length, level).AdjustForSize(source.Length);
        return FindMatches(source, store, repeatOffsets, table);
    }

    /// <summary>
    /// Parses one frame block with an explicitly supplied (already adjusted)
    /// parameter row (see <see cref="ZstdMatchFinder.FindMatches(ReadOnlySpan{byte}, ZstdSequenceStore, uint[], ZstdCompressionParameters)"/> for why
    /// multi-block frames share the frame-level row). Single-block path:
    /// fresh tables and fresh statistics, exactly like a native first block.
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets,
        ZstdCompressionParameters table)
    {
        var n = source.Length;
        if (n == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        var optLevel = OptLevelFor(table.Strategy);
        var stats = new OptStats(); // fresh: litLengthSum == 0 (first block init)

        if (table.Strategy == ZstdStrategy.BtUltra2 && n > PredefThreshold)
        {
            // Two-pass stats seeding (ZSTD_initStats_ultra): a throwaway pass
            // collects statistics; the real pass runs over fresh tables with
            // the seeded stats (equivalent to the native window-shift
            // invalidation, whose stale entries all sit below lowLimit).
            var tmpStore = new ZstdSequenceStore(n);
            var tmpRep = (uint[])repeatOffsets.Clone();
            var tmpNext = 0;
            OptGeneric(source, 0, n, tmpStore, tmpRep, table, optLevel, stats, NewTables(table), ref tmpNext);
        }

        var nextToUpdate = 0;
        return OptGeneric(source, 0, n, store, repeatOffsets, table, optLevel, stats, NewTables(table), ref nextToUpdate);
    }

    /// <summary>
    /// Parses one frame block <c>[blockStart, blockEnd)</c> of the frame held
    /// by <paramref name="state"/> with the frame-persistent binary-tree
    /// tables, statistics and update cursor — the native
    /// <c>ZSTD_MatchState_t</c> + <c>ms-&gt;opt</c> contract across blocks:
    /// later blocks scale down the accumulated statistics
    /// (<c>ZSTD_rescaleFreqs</c> non-first arm) instead of re-initializing
    /// them, and the btultra2 two-pass seeding runs only on the first block
    /// (<c>litLengthSum == 0</c> at frame start, like native's
    /// <c>curr == dictLimit</c> gate). Positions are absolute frame offsets;
    /// matches may reference earlier blocks but never extend past
    /// <paramref name="blockEnd"/> (native <c>iend</c>).
    /// </summary>
    internal static int FindMatches(
        ZstdFrameState state, int blockStart, int blockEnd,
        ZstdSequenceStore store, uint[] repeatOffsets)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (blockEnd == blockStart)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        var table = state.Prm;
        var optLevel = OptLevelFor(table.Strategy);
        var stats = state.OptStats();
        var tables = state.OptTables();

        if (table.Strategy == ZstdStrategy.BtUltra2
            && stats.LitLengthSum == 0 && blockStart == 0
            && blockEnd - blockStart > PredefThreshold)
        {
            // First-block two-pass seeding (ZSTD_initStats_ultra): the
            // throwaway pass runs over temp tables (discarded, like the
            // native window-shift invalidation) sharing the frame statistics;
            // the real pass below refills the still-empty persistent tables.
            var tmpStore = new ZstdSequenceStore(blockEnd - blockStart);
            var tmpRep = (uint[])repeatOffsets.Clone();
            var tmpNext = 0;
            OptGeneric(state.Frame, blockStart, blockEnd, tmpStore, tmpRep, table, optLevel, stats, NewTables(table), ref tmpNext);
        }

        return OptGeneric(state.Frame, blockStart, blockEnd, store, repeatOffsets, table, optLevel, stats, tables, ref state.NextToUpdate);
    }

    private static int OptLevelFor(ZstdStrategy strategy)
    {
        return strategy switch
        {
            ZstdStrategy.BtOpt => 0,
            ZstdStrategy.BtUltra or ZstdStrategy.BtUltra2 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), $"Strategy {strategy} is not optimal-parsing."),
        };
    }

    /// <summary>
    /// Fresh binary-tree tables for one optimal-parser pass
    /// (<c>hashLog</c>, <c>chainLog</c>, and the 3-byte table when
    /// <c>minMatch</c> is 3).
    /// </summary>
    private static (uint[] Hash, uint[] Bt, uint[] Hash3) NewTables(ZstdCompressionParameters prm)
    {
        var hash = new uint[1 << prm.HashLog];
        var bt = new uint[1 << prm.ChainLog];
        var hashLog3 = HashLog3For(prm);
        uint[] hash3 = hashLog3 > 0 ? new uint[1 << hashLog3] : [];
        return (hash, bt, hash3);
    }

    /// <summary>
    /// The 3-byte hash log for <paramref name="prm"/>
    /// (<c>MIN(17, windowLog)</c> when <c>minMatch</c> is 3, else 0 = absent).
    /// </summary>
    internal static int HashLog3For(ZstdCompressionParameters prm)
    {
        return prm.MinMatch == 3 ? Math.Min(HashLog3Max, prm.WindowLog) : 0;
    }

    // ------------------------------------------------------------------
    // Price machinery
    // ------------------------------------------------------------------

    private static uint BitWeight(uint stat)
    {
        return (uint)Highbit32(stat + 1u) * BitcostMultiplier;
    }

    private static uint FracWeight(uint rawStat)
    {
        var stat = rawStat + 1;
        var hb = Highbit32(stat);
        var bWeight = (uint)hb * BitcostMultiplier;
        var fWeight = (stat << 8) >> hb;
        return bWeight + fWeight;
    }

    private static uint Weight(uint stat, int optLevel)
    {
        return optLevel == 0 ? BitWeight(stat) : FracWeight(stat);
    }

    private static uint SumU32(uint[] table, int count)
    {
        uint total = 0;
        for (var i = 0; i < count; i++)
        {
            total += table[i];
        }

        return total;
    }

    private static uint DownscaleStats(uint[] table, uint lastEltIndex, uint shift, bool base1)
    {
        uint sum = 0;
        for (uint s = 0; s <= lastEltIndex; s++)
        {
            var @base = base1 ? 1u : (table[s] > 0 ? 1u : 0u);
            var newStat = @base + (table[s] >> (int)shift);
            sum += newStat;
            table[s] = newStat;
        }

        return sum;
    }

    private static uint ScaleStats(uint[] table, uint lastEltIndex, uint logTarget)
    {
        var prevSum = SumU32(table, (int)lastEltIndex + 1);
        var factor = prevSum >> (int)logTarget;
        if (factor <= 1)
        {
            return prevSum;
        }

        return DownscaleStats(table, lastEltIndex, (uint)Highbit32(factor), base1: true);
    }

    private static void RescaleFreqs(OptStats o, ReadOnlySpan<byte> src, int optLevel)
    {
        if (o.LitLengthSum == 0)
        {
            // First block: init from the raw block (no dictionary single-shot).
            o.Predef = src.Length <= PredefThreshold;
            for (var i = 0; i < src.Length; i++)
            {
                o.LitFreq[src[i]]++;
            }

            o.LitSum = DownscaleStats(o.LitFreq, 255, 8, base1: false);

            BaseLitLengthFreqs.CopyTo(o.LitLengthFreq, 0);
            o.LitLengthSum = SumU32(BaseLitLengthFreqs, BaseLitLengthFreqs.Length);

            for (var ml = 0; ml < o.MatchLengthFreq.Length; ml++)
            {
                o.MatchLengthFreq[ml] = 1;
            }

            o.MatchLengthSum = (uint)o.MatchLengthFreq.Length;

            BaseOffCodeFreqs.CopyTo(o.OffCodeFreq, 0);
            o.OffCodeSum = SumU32(BaseOffCodeFreqs, BaseOffCodeFreqs.Length);
        }
        else
        {
            // Later block: scale down (only reachable via the ultra2 first
            // pass sharing stats with the real pass).
            o.LitSum = ScaleStats(o.LitFreq, 255, 12);
            o.LitLengthSum = ScaleStats(o.LitLengthFreq, 35, 11);
            o.MatchLengthSum = ScaleStats(o.MatchLengthFreq, 52, 11);
            o.OffCodeSum = ScaleStats(o.OffCodeFreq, 31, 11);
        }

        SetBasePrices(o, optLevel);
    }

    private static void SetBasePrices(OptStats o, int optLevel)
    {
        o.LitSumBasePrice = Weight(o.LitSum, optLevel);
        o.LitLengthSumBasePrice = Weight(o.LitLengthSum, optLevel);
        o.MatchLengthSumBasePrice = Weight(o.MatchLengthSum, optLevel);
        o.OffCodeSumBasePrice = Weight(o.OffCodeSum, optLevel);
    }

    private static uint RawLiteralsCost(ReadOnlySpan<byte> src, int pos, uint litLength, OptStats o, int optLevel)
    {
        if (litLength == 0)
        {
            return 0;
        }

        if (o.Predef)
        {
            return litLength * 6u * BitcostMultiplier;
        }

        unchecked
        {
            var price = o.LitSumBasePrice * litLength;
            var litPriceMax = o.LitSumBasePrice - BitcostMultiplier;
            for (uint u = 0; u < litLength; u++)
            {
                var litPrice = Weight(o.LitFreq[src[pos + (int)u]], optLevel);
                if (litPrice > litPriceMax)
                {
                    litPrice = litPriceMax;
                }

                price -= litPrice;
            }

            return price;
        }
    }

    private static uint LitLengthPrice(uint litLength, OptStats o, int optLevel)
    {
        if (o.Predef)
        {
            return Weight(litLength, optLevel);
        }

        var llCode = ZstdBlockEncoder.LLcode(litLength);
        return ((uint)ZstdBlockEncoder.LlExtraBits(llCode) * BitcostMultiplier)
            + o.LitLengthSumBasePrice
            - Weight(o.LitLengthFreq[llCode], optLevel);
    }

    private static uint GetMatchPrice(uint offBase, uint matchLength, OptStats o, int optLevel)
    {
        var offCode = (uint)Highbit32(offBase);
        var mlBase = matchLength - (uint)ZstdSeq.MinMatch;

        if (o.Predef)
        {
            return Weight(mlBase, optLevel) + ((16u + offCode) * BitcostMultiplier);
        }

        unchecked
        {
            var price = (offCode * BitcostMultiplier)
                + (o.OffCodeSumBasePrice - Weight(o.OffCodeFreq[offCode], optLevel));
            if (optLevel < 2 && offCode >= 20)
            {
                price += (offCode - 19u) * 2u * BitcostMultiplier;
            }

            var mlCode = ZstdBlockEncoder.MLcode(mlBase);
            price += ((uint)ZstdBlockEncoder.MlExtraBits(mlCode) * BitcostMultiplier)
                + (o.MatchLengthSumBasePrice - Weight(o.MatchLengthFreq[mlCode], optLevel));
            price += BitcostMultiplier / 5;
            return price;
        }
    }

    private static void UpdateStats(
        OptStats o, uint litLength, ReadOnlySpan<byte> src, int litPos,
        uint offBase, uint matchLength)
    {
        for (uint u = 0; u < litLength; u++)
        {
            o.LitFreq[src[litPos + (int)u]] += LitfreqAdd;
        }

        o.LitSum += litLength * LitfreqAdd;

        var llCode = ZstdBlockEncoder.LLcode(litLength);
        o.LitLengthFreq[llCode]++;
        o.LitLengthSum++;

        var offCode = (uint)Highbit32(offBase);
        o.OffCodeFreq[offCode]++;
        o.OffCodeSum++;

        var mlCode = ZstdBlockEncoder.MLcode(matchLength - (uint)ZstdSeq.MinMatch);
        o.MatchLengthFreq[mlCode]++;
        o.MatchLengthSum++;
    }

    private static uint Hash3(ReadOnlySpan<byte> src, int pos, int hBits)
    {
        var u = Read32LE(src, pos);
        return ((u << 8) * Prime3Bytes) >> (32 - hBits);
    }

    private static uint ReadMinMatch(ReadOnlySpan<byte> src, int pos, int minMatch)
    {
        var v = Read32LE(src, pos);
        return minMatch == 3 ? v << 8 : v;
    }

    // ------------------------------------------------------------------
    // Binary-tree match enumeration (opt variant)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ZSTD_insertBt1</c>, noDict: inserts <paramref name="curr"/> into the
    /// tree, tracking the longest match. Returns how many positions the tree
    /// updater may skip (<c>MAX(positions, matchEndIdx - (curr + 8))</c> ≥ 1).
    /// </summary>
    private static uint InsertBt1(
        ReadOnlySpan<byte> src, int end, int curr, int mls, int target,
        int hashLog, int chainLog, int windowLog, int searchLog,
        uint[] hashTable, uint[] bt)
    {
        var h = ZstdMatchFinder.HashPtr(src, curr, hashLog, mls);
        var matchIndex = SlotPos(hashTable[h]);
        var btMask = (1 << (chainLog - 1)) - 1;
        var btLow = btMask >= curr ? 0 : curr - btMask;
        int? smallerSlot = 2 * (curr & btMask);
        int? largerSlot = smallerSlot + 1;
        var windowLowT = WindowLow(target, windowLog);
        var matchEndIdx = curr + 8 + 1;
        var bestLength = 8;
        var nbCompares = (uint)(1 << searchLog);
        var commonSmaller = 0;
        var commonLarger = 0;

        hashTable[h] = (uint)curr + 1; // +1: 0 means empty

        for (; nbCompares > 0 && matchIndex >= windowLowT; nbCompares--)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            var matchLength = Math.Min(commonSmaller, commonLarger);
            matchLength += CountMatches(src, curr + matchLength, matchIndex + matchLength, end);

            if (matchLength > bestLength)
            {
                bestLength = matchLength;
                if (matchLength > matchEndIdx - matchIndex)
                {
                    matchEndIdx = matchIndex + matchLength;
                }
            }

            if (curr + matchLength == end)
            {
                break; // drop, to guarantee consistency
            }

            if (src[matchIndex + matchLength] < src[curr + matchLength])
            {
                if (smallerSlot.HasValue)
                {
                    bt[smallerSlot.Value] = (uint)matchIndex + 1;
                }

                commonSmaller = matchLength;
                if (matchIndex <= btLow)
                {
                    smallerSlot = null;
                    break;
                }

                smallerSlot = nextSlot + 1;
                matchIndex = SlotPos(bt[nextSlot + 1]);
            }
            else
            {
                if (largerSlot.HasValue)
                {
                    bt[largerSlot.Value] = (uint)matchIndex + 1;
                }

                commonLarger = matchLength;
                if (matchIndex <= btLow)
                {
                    largerSlot = null;
                    break;
                }

                largerSlot = nextSlot;
                matchIndex = SlotPos(bt[nextSlot]);
            }
        }

        if (smallerSlot.HasValue)
        {
            bt[smallerSlot.Value] = 0;
        }

        if (largerSlot.HasValue)
        {
            bt[largerSlot.Value] = 0;
        }

        uint positions = 0;
        if (bestLength > 384)
        {
            positions = (uint)Math.Min(192, bestLength - 384);
        }

        return Math.Max(positions, (uint)(matchEndIdx - (curr + 8)));
    }

    private static void UpdateTree(
        ReadOnlySpan<byte> src, int end, int target, int mls,
        int hashLog, int chainLog, int windowLog, int searchLog,
        uint[] hashTable, uint[] bt, ref int nextToUpdate)
    {
        var idx = nextToUpdate;
        while (idx < target)
        {
            var forward = InsertBt1(
                src, end, idx, mls, target, hashLog, chainLog, windowLog, searchLog,
                hashTable, bt);
            idx += (int)forward;
        }

        nextToUpdate = target;
    }

    /// <summary>
    /// <c>ZSTD_insertBtAndGetAllMatches</c>, noDict: repcode probes, the
    /// 3-byte hash probe (mls 3), then the tree walk collecting strictly
    /// improving matches. Returns the candidate count.
    /// </summary>
    private static uint InsertBtAndGetAllMatches(
        OptMatch[] matches, ReadOnlySpan<byte> src, int end, int curr,
        int minMatch, int mls, int sufficientLen,
        int hashLog, int chainLog, int windowLog, int searchLog, int hashLog3,
        uint[] hashTable, uint[] bt, uint[] hashTable3,
        ref int nextToUpdate, ref int nextToUpdate3,
        uint rep0, uint rep1, uint rep2, uint ll0, uint lengthToBeat)
    {
        var btMask = (1 << (chainLog - 1)) - 1;
        var btLow = btMask >= curr ? 0 : curr - btMask;
        var windowLow = WindowLow(curr, windowLog);
        var matchLow = windowLow;
        int? smallerSlot = 2 * (curr & btMask);
        int? largerSlot = smallerSlot + 1;
        var matchEndIdx = curr + 8 + 1;
        var mnum = 0u;
        var nbCompares = (uint)(1 << searchLog);
        var commonSmaller = 0;
        var commonLarger = 0;
        var bestLength = (int)lengthToBeat - 1;

        // Repcode probes (repCode ll0..2+ll0).
        {
            var lastR = 3 + ll0;
            for (var repCode = ll0; repCode < lastR; repCode++)
            {
                var repOffset = repCode == 3 ? rep0 - 1 : repCode switch
                {
                    0 => rep0,
                    1 => rep1,
                    _ => rep2,
                };
                var repLen = 0;
                if (repOffset > 0 && unchecked(repOffset - 1) < (uint)curr)
                {
                    var repIndex = curr - (int)repOffset;
                    if (repIndex >= windowLow
                        && ReadMinMatch(src, curr, minMatch) == ReadMinMatch(src, repIndex, minMatch))
                    {
                        repLen = CountMatches(src, curr + minMatch, curr + minMatch - (int)repOffset, end) + minMatch;
                    }
                }

                if (repLen > bestLength)
                {
                    bestLength = repLen;
                    matches[mnum] = new OptMatch(ZstdSeq.Repcode1 + repCode - ll0, (uint)repLen);
                    mnum++;
                    if (repLen > sufficientLen || curr + repLen == end)
                    {
                        return mnum; // best possible
                    }
                }
            }
        }

        // 3-byte hash probe (mls 3 only).
        if (mls == 3 && bestLength < mls)
        {
            while (nextToUpdate3 < curr)
            {
                hashTable3[Hash3(src, nextToUpdate3, hashLog3)] = (uint)nextToUpdate3 + 1;
                nextToUpdate3++;
            }

            var matchIndex3 = SlotPos(hashTable3[Hash3(src, curr, hashLog3)]);
            if (matchIndex3 >= matchLow && curr - matchIndex3 < 1 << 18)
            {
                var mlen = CountMatches(src, curr, matchIndex3, end);
                if (mlen >= mls)
                {
                    bestLength = mlen;
                    matches[0] = new OptMatch(ZstdSeq.OffsetToOffBase((uint)(curr - matchIndex3)), (uint)mlen);
                    mnum = 1;
                    if (mlen > sufficientLen || curr + mlen == end)
                    {
                        nextToUpdate = curr + 1; // skip insertion
                        return 1;
                    }
                }
            }
        }

        var h = ZstdMatchFinder.HashPtr(src, curr, hashLog, mls);
        var matchIndex = SlotPos(hashTable[h]);
        hashTable[h] = (uint)curr + 1; // Update Hash Table

        for (; nbCompares > 0 && matchIndex >= matchLow; nbCompares--)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            var matchLength = Math.Min(commonSmaller, commonLarger);
            matchLength += CountMatches(src, curr + matchLength, matchIndex + matchLength, end);

            if (matchLength > bestLength)
            {
                if (matchLength > matchEndIdx - matchIndex)
                {
                    matchEndIdx = matchIndex + matchLength;
                }

                bestLength = matchLength;
                matches[mnum] = new OptMatch(ZstdSeq.OffsetToOffBase((uint)(curr - matchIndex)), (uint)matchLength);
                mnum++;
                if (matchLength > OptNum || curr + matchLength == end)
                {
                    break; // drop, to preserve bt consistency
                }
            }

            if (src[matchIndex + matchLength] < src[curr + matchLength])
            {
                if (smallerSlot.HasValue)
                {
                    bt[smallerSlot.Value] = (uint)matchIndex + 1;
                }

                commonSmaller = matchLength;
                if (matchIndex <= btLow)
                {
                    smallerSlot = null;
                    break;
                }

                smallerSlot = nextSlot + 1;
                matchIndex = SlotPos(bt[nextSlot + 1]);
            }
            else
            {
                if (largerSlot.HasValue)
                {
                    bt[largerSlot.Value] = (uint)matchIndex + 1;
                }

                commonLarger = matchLength;
                if (matchIndex <= btLow)
                {
                    largerSlot = null;
                    break;
                }

                largerSlot = nextSlot;
                matchIndex = SlotPos(bt[nextSlot]);
            }
        }

        if (smallerSlot.HasValue)
        {
            bt[smallerSlot.Value] = 0;
        }

        if (largerSlot.HasValue)
        {
            bt[largerSlot.Value] = 0;
        }

        nextToUpdate = matchEndIdx - 8; // skip repetitive patterns
        return mnum;
    }

    private static uint BtGetAllMatches(
        OptMatch[] matches, ReadOnlySpan<byte> src, int end, int ip,
        int minMatch, int mls, int sufficientLen,
        int hashLog, int chainLog, int windowLog, int searchLog, int hashLog3,
        uint[] hashTable, uint[] bt, uint[] hashTable3,
        ref int nextToUpdate, ref int nextToUpdate3,
        uint rep0, uint rep1, uint rep2, uint ll0, uint lengthToBeat)
    {
        if (ip < nextToUpdate)
        {
            return 0; // skipped area
        }

        UpdateTree(src, end, ip, mls, hashLog, chainLog, windowLog, searchLog,
            hashTable, bt, ref nextToUpdate);
        return InsertBtAndGetAllMatches(
            matches, src, end, ip, minMatch, mls, sufficientLen,
            hashLog, chainLog, windowLog, searchLog, hashLog3,
            hashTable, bt, hashTable3,
            ref nextToUpdate, ref nextToUpdate3,
            rep0, rep1, rep2, ll0, lengthToBeat);
    }

    // ------------------------------------------------------------------
    // Optimal parser
    // ------------------------------------------------------------------

    private static int OptGeneric(
        ReadOnlySpan<byte> src, int blockStart, int blockEnd,
        ZstdSequenceStore store, uint[] rep,
        ZstdCompressionParameters prm, int optLevel, OptStats stats,
        (uint[] Hash, uint[] Bt, uint[] Hash3) tables, ref int nextToUpdate)
    {
        var targetLength = prm.TargetLength;
        var minMatch = prm.MinMatch == 3 ? 3 : 4;
        var mls = Math.Clamp(prm.MinMatch, 3, 6);
        var hashLog = prm.HashLog;
        var chainLog = prm.ChainLog;
        var windowLog = prm.WindowLog;
        var searchLog = prm.SearchLog;
        var sufficientLen = Math.Min(targetLength, OptNum - 1);
        var (hashTable, bt, hashTable3) = tables;
        var hashLog3 = HashLog3For(prm);
        var nextToUpdate3 = nextToUpdate; // native: local per block, seeded from ms->nextToUpdate
        var opt = new Optimal[OptSize];
        var matches = new OptMatch[OptSize];
        var ilimit = blockEnd - 8;

        RescaleFreqs(stats, src.Slice(blockStart, blockEnd - blockStart), optLevel);
        var anchor = blockStart;
        var ip = blockStart;
        if (blockStart == 0)
        {
            ip++; // ip == prefixStart (frame start; later blocks never equal it)
        }

        while (ip < ilimit)
        {
            var cur = 0;
            var lastPos = 0;

            // Find first match.
            {
                var litlen = (uint)(ip - anchor);
                var ll0 = litlen == 0 ? 1u : 0u;
                var nbMatches = BtGetAllMatches(
                    matches, src, blockEnd, ip, minMatch, mls, sufficientLen,
                    hashLog, chainLog, windowLog, searchLog, hashLog3,
                    hashTable, bt, hashTable3,
                    ref nextToUpdate, ref nextToUpdate3,
                    rep[0], rep[1], rep[2], ll0, (uint)minMatch);
                if (nbMatches == 0)
                {
                    ip++;
                    continue;
                }

                // Initialize opt[0].
                opt[0].Mlen = 0;
                opt[0].Litlen = litlen;
                opt[0].Price = (int)LitLengthPrice(litlen, stats, optLevel);
                opt[0].Rep0 = rep[0];
                opt[0].Rep1 = rep[1];
                opt[0].Rep2 = rep[2];

                // Large match -> immediate encoding.
                var maxMl = matches[nbMatches - 1].Len;
                var maxOff = matches[nbMatches - 1].Off;
                if (maxMl > (uint)sufficientLen)
                {
                    StoreShortestPath(src, store, rep, stats, opt, optLevel,
                        ref anchor, ref ip, 0, (int)maxMl, maxMl, maxOff, 0);
                    continue;
                }

                // Set prices for first matches starting at position 0.
                var pos = 0u;
                for (pos = 1; pos < (uint)minMatch; pos++)
                {
                    opt[(int)pos].Price = MaxPrice;
                    opt[(int)pos].Mlen = 0;
                    opt[(int)pos].Litlen = litlen + pos;
                }

                for (uint matchNb = 0; matchNb < nbMatches; matchNb++)
                {
                    var offBase = matches[matchNb].Off;
                    var end = matches[matchNb].Len;
                    for (; pos <= end; pos++)
                    {
                        var matchPrice = (int)GetMatchPrice(offBase, pos, stats, optLevel);
                        var sequencePrice = opt[0].Price + matchPrice;
                        opt[(int)pos].Mlen = pos;
                        opt[(int)pos].Off = offBase;
                        opt[(int)pos].Litlen = 0;
                        opt[(int)pos].Price = sequencePrice + (int)LitLengthPrice(0, stats, optLevel);
                    }
                }

                lastPos = (int)pos - 1;
                opt[(int)pos].Price = MaxPrice;
            }

            // Check further positions.
            for (cur = 1; cur <= lastPos; cur++)
            {
                var inr = ip + cur;

                // Fix current position with one literal if cheaper.
                if (opt[cur - 1].Litlen != FillLitlen)
                {
                    var litlen = opt[cur - 1].Litlen + 1;
                    var price = opt[cur - 1].Price
                        + (int)RawLiteralsCost(src, inr - 1, 1, stats, optLevel)
                        + LitIncPrice(litlen, stats, optLevel);
                    if (price <= opt[cur].Price)
                    {
                        var prevMatch = opt[cur];
                        opt[cur] = opt[cur - 1];
                        opt[cur].Litlen = litlen;
                        opt[cur].Price = price;
                        if (optLevel >= 1
                            && prevMatch.Litlen == 0
                            && LitIncPrice(1, stats, optLevel) < 0
                            && inr < blockEnd)
                        {
                            var with1literal = prevMatch.Price
                                + (int)RawLiteralsCost(src, inr, 1, stats, optLevel)
                                + LitIncPrice(1, stats, optLevel);
                            var withMoreLiterals = price
                                + (int)RawLiteralsCost(src, inr, 1, stats, optLevel)
                                + LitIncPrice(litlen + 1, stats, optLevel);
                            if (with1literal < withMoreLiterals
                                && with1literal < opt[cur + 1].Price)
                            {
                                var prev = cur - (int)prevMatch.Mlen;
                                var newReps = ZstdSeq.NewRep(
                                    [opt[prev].Rep0, opt[prev].Rep1, opt[prev].Rep2],
                                    prevMatch.Off, opt[prev].Litlen == 0 ? 1u : 0u);
                                opt[cur + 1] = prevMatch;
                                opt[cur + 1].Rep0 = newReps[0];
                                opt[cur + 1].Rep1 = newReps[1];
                                opt[cur + 1].Rep2 = newReps[2];
                                opt[cur + 1].Litlen = 1;
                                opt[cur + 1].Price = with1literal;
                                if (lastPos < cur + 1)
                                {
                                    lastPos = cur + 1;
                                }
                            }
                        }
                    }
                }

                // Offset history is not updated during match comparison.
                if (opt[cur].Litlen == 0)
                {
                    var prev = cur - (int)opt[cur].Mlen;
                    var newReps = ZstdSeq.NewRep(
                        [opt[prev].Rep0, opt[prev].Rep1, opt[prev].Rep2],
                        opt[cur].Off, opt[prev].Litlen == 0 ? 1u : 0u);
                    opt[cur].Rep0 = newReps[0];
                    opt[cur].Rep1 = newReps[1];
                    opt[cur].Rep2 = newReps[2];
                }

                // Last match must start at a minimum distance of 8 from oend.
                if (inr > ilimit)
                {
                    continue;
                }

                if (cur == lastPos)
                {
                    break;
                }

                if (optLevel == 0
                    && opt[cur + 1].Price <= opt[cur].Price + (BitcostMultiplier / 2))
                {
                    continue; // skip unpromising positions
                }

                {
                    var ll0 = opt[cur].Litlen == 0 ? 1u : 0u;
                    var previousPrice = opt[cur].Price;
                    var basePrice = previousPrice + (int)LitLengthPrice(0, stats, optLevel);
                    var nbMatches = BtGetAllMatches(
                        matches, src, blockEnd, inr, minMatch, mls, sufficientLen,
                        hashLog, chainLog, windowLog, searchLog, hashLog3,
                        hashTable, bt, hashTable3,
                        ref nextToUpdate, ref nextToUpdate3,
                        opt[cur].Rep0, opt[cur].Rep1, opt[cur].Rep2, ll0, (uint)minMatch);

                    if (nbMatches == 0)
                    {
                        continue;
                    }

                    var longestMl = matches[nbMatches - 1].Len;
                    if (longestMl > (uint)sufficientLen
                        || cur + (int)longestMl >= OptNum
                        || inr + (int)longestMl >= blockEnd)
                    {
                        StoreShortestPath(src, store, rep, stats, opt, optLevel,
                            ref anchor, ref ip, cur, cur + (int)longestMl,
                            longestMl, matches[nbMatches - 1].Off, 0);
                        goto NextSeries;
                    }

                    // Set prices using matches found at position == cur.
                    for (uint matchNb = 0; matchNb < nbMatches; matchNb++)
                    {
                        var offset = matches[matchNb].Off;
                        var lastMl = matches[matchNb].Len;
                        var startMl = matchNb > 0 ? matches[matchNb - 1].Len + 1 : (uint)minMatch;
                        for (var mlen = lastMl; mlen >= startMl; mlen--)
                        {
                            var pos = cur + (int)mlen;
                            var price = basePrice + (int)GetMatchPrice(offset, mlen, stats, optLevel);
                            if (pos > lastPos || price < opt[pos].Price)
                            {
                                while (lastPos < pos)
                                {
                                    lastPos++;
                                    opt[lastPos].Price = MaxPrice;
                                    opt[lastPos].Litlen = FillLitlen;
                                }

                                opt[pos].Mlen = mlen;
                                opt[pos].Off = offset;
                                opt[pos].Litlen = 0;
                                opt[pos].Price = price;
                            }
                            else if (optLevel == 0)
                            {
                                break; // early update abort
                            }
                        }
                    }

                    opt[lastPos + 1].Price = MaxPrice;
                }
            }

            {
                var lastStretch = opt[lastPos];
                StoreShortestPath(src, store, rep, stats, opt, optLevel,
                    ref anchor, ref ip, lastPos - (int)lastStretch.Mlen, lastPos,
                    lastStretch.Mlen, lastStretch.Off, lastStretch.Litlen);
            }

        NextSeries:;
        }

        store.SetTrailingLiterals(src.Slice(anchor, blockEnd - anchor));
        return blockEnd - anchor;
    }

    private static int LitIncPrice(uint litlen, OptStats o, int optLevel)
    {
        return (int)LitLengthPrice(litlen, o, optLevel) - (int)LitLengthPrice(litlen - 1, o, optLevel);
    }

    /// <summary>
    /// Shortest-path tail: rep update, reverse traversal converting stretches
    /// to sequences, sequence store + stats update. (<c>_shortestPath</c>.)
    /// </summary>
    private static void StoreShortestPath(
        ReadOnlySpan<byte> src, ZstdSequenceStore store, uint[] rep, OptStats stats,
        Optimal[] opt, int optLevel,
        ref int anchor, ref int ip, int cur, int lastPos,
        uint stretchMlen, uint stretchOff, uint stretchLitlen)
    {
        if (stretchMlen == 0)
        {
            // No solution: all matches converted into literals.
            ip += lastPos;
            return;
        }

        // Update offset history.
        if (stretchLitlen == 0)
        {
            var newReps = ZstdSeq.NewRep(
                [opt[cur].Rep0, opt[cur].Rep1, opt[cur].Rep2],
                stretchOff, opt[cur].Litlen == 0 ? 1u : 0u);
            rep[0] = newReps[0];
            rep[1] = newReps[1];
            rep[2] = newReps[2];
        }
        else
        {
            var lastRep = opt[lastPos];
            rep[0] = lastRep.Rep0;
            rep[1] = lastRep.Rep1;
            rep[2] = lastRep.Rep2;
            cur -= (int)stretchLitlen;
        }

        var storeEnd = cur + 2;
        int storeStart;
        var stretchPos = cur;
        var lastStretch = new Optimal { Litlen = stretchLitlen, Mlen = stretchMlen, Off = stretchOff };
        if (lastStretch.Litlen > 0)
        {
            opt[storeEnd].Litlen = lastStretch.Litlen;
            opt[storeEnd].Mlen = 0;
            storeStart = storeEnd - 1;
            opt[storeStart] = lastStretch;
        }

        opt[storeEnd] = lastStretch;
        storeStart = storeEnd;
        while (true)
        {
            var nextStretch = opt[stretchPos];
            opt[storeStart].Litlen = nextStretch.Litlen;
            if (nextStretch.Mlen == 0)
            {
                break; // reaching beginning of segment
            }

            storeStart--;
            opt[storeStart] = nextStretch;
            stretchPos -= (int)(nextStretch.Litlen + nextStretch.Mlen);
        }

        for (var storePos = storeStart; storePos <= storeEnd; storePos++)
        {
            var llen = opt[storePos].Litlen;
            var mlen = opt[storePos].Mlen;
            var offBase = opt[storePos].Off;
            if (mlen == 0)
            {
                ip = anchor + (int)llen;
                continue;
            }

            UpdateStats(stats, llen, src, anchor, offBase, mlen);
            store.StoreSequence(src.Slice(anchor, (int)llen), offBase, (int)mlen);
            anchor += (int)(llen + mlen);
            ip = anchor;
        }

        SetBasePrices(stats, optLevel);
    }
}
