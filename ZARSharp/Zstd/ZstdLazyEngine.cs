using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// Exact C# port of <c>ZSTD_compressBlock_lazy_generic</c> with depth 0
/// (greedy), 1 (lazy) or 2 (lazy2, btlazy2) over
/// <c>ZSTD_RowFindBestMatch</c> (row hash), <c>ZSTD_HcFindBestMatch</c> (hash
/// chain) or <see cref="ZstdBinaryTree"/> (binary tree), all
/// <c>ZSTD_noDict</c> (<c>lib/compress/zstd_lazy.c</c>, zstd-1.5.7). Covers the
/// <see cref="ZstdStrategy.Greedy"/>, <see cref="ZstdStrategy.Lazy"/>,
/// <see cref="ZstdStrategy.Lazy2"/> and <see cref="ZstdStrategy.BtLazy2"/>
/// tier rows. The tier row comes from
/// <see cref="ZstdCompressionParameters.ForSizeAndLevel"/> plus
/// <see cref="ZstdCompressionParameters.AdjustForSize"/>; the frame header
/// uses the same adjusted window log. Row mode follows
/// <c>ZSTD_resolveRowMatchFinderMode</c>: row hash when the adjusted
/// <c>windowLog &gt; 14</c>, hash chain otherwise.
/// <para/>
/// Faithful details: <c>offsetSaved1/2</c> invalidation at block start,
/// <c>ilimit</c> shortening for the row path (<c>iend-8-8</c>), the 384/96/32
/// row-insert skip rule, tag-buffer-then-longest candidate order (head-matchPos-0
/// skip, lowLimit break, attempts capped at <c>min(searchLog,rowLog)</c>),
/// gain tiebreaks with <c>ZSTD_highbit32</c>, catch-up bounded strictly above
/// the prefix start, the immediate-repcode loop, and end-of-block rep sync.
/// Deliberate non-ports (behavior-neutral for fresh single-shot contexts):
/// prefetch hints, the hash <em>cache</em> (memo only — every hash is
/// recomputed directly with identical values), and <c>hashSaltEntropy</c>
/// accumulation (only affects reused contexts; fresh salt is the constant
/// <see cref="FreshHashSalt"/>). Prefilter over-reads past the block end are
/// guarded: native reads heap garbage there (match with probability 2⁻³²);
/// the port skips such candidates.
/// Index discipline: table slots store <c>pos + 1</c> (never bare <c>pos</c>),
/// mirroring upstream where absolute indices start at
/// <c>ZSTD_WINDOW_START_INDEX (2)</c> and slot value 0 means "empty".
/// </summary>
internal static class ZstdLazyEngine
{
    private const int RowHashTagBits = 8; // ZSTD_ROW_HASH_TAG_BITS (zstd_lazy.h).
    private const int RowHashTagMask = (1 << RowHashTagBits) - 1;
    private const int RowHashCacheSize = 8; // ZSTD_ROW_HASH_CACHE_SIZE.
    private const uint Prime4 = 2654435761U;
    private const ulong Prime5 = 889523592379UL;
    private const ulong Prime6 = 227718039650203UL;
    private const ulong Prime7 = 58295818150454627UL;
    private const ulong Prime8 = 0xCF1BBCDCB7A56463UL;

    /// <summary>
    /// Fresh-context row hash salt: <c>ZSTD_advanceHashSalt</c> from zero
    /// (<c>ZSTD_bitmix(0,8) ^ ZSTD_bitmix(0,4)</c>). One-shot
    /// <c>ZSTD_compress</c> always starts here, so native output is
    /// deterministic and this constant reproduces it.
    /// </summary>
    internal static readonly ulong FreshHashSalt = Bitmix(0, 8) ^ Bitmix(0, 4);

    private static ulong RotateRight64(ulong value, int count)
    {
        return (value >> count) | (value << (64 - count));
    }

    private static ulong Bitmix(ulong value, ulong length)
    {
        // ZSTD_bitmix (zstd_compress.c): XXH3_rrmxmx-based.
        value ^= RotateRight64(value, 49) ^ RotateRight64(value, 24);
        value *= 0x9FB21C651E98DF25UL;
        value ^= (value >> 35) + length;
        value *= 0x9FB21C651E98DF25UL;
        return value ^ (value >> 28);
    }

    private static uint Read32LE(ReadOnlySpan<byte> src, int pos)
    {
        return (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));
    }

    private static ulong Read64LE(ReadOnlySpan<byte> src, int pos)
    {
        var lo = (ulong)Read32LE(src, pos);
        var hi = (ulong)Read32LE(src, pos + 4);
        return lo | (hi << 32);
    }

    /// <summary>
    /// <c>ZSTD_hashPtrSalted</c> for <paramref name="hBits"/> ≤ 32. The salt is
    /// the low 32 bits for 4-byte hashes, full 64 bits otherwise. All hashed
    /// positions on the row path are at least 8 bytes inside the block, so
    /// reads are strict; hash-chain tail inserts are zero-padded past the end
    /// (native over-reads heap garbage there — behavior-neutral, see class
    /// remarks).
    /// </summary>
    internal static uint HashSalted(ReadOnlySpan<byte> src, int pos, int hBits, int minMatch, ulong salt)
    {
        if (minMatch <= 4)
        {
            var u = pos + 4 <= src.Length ? Read32LE(src, pos) : ReadPadded32(src, pos);
            return (uint)(((u * Prime4) ^ (uint)salt) >> (32 - hBits));
        }

        var value = pos + 8 <= src.Length ? Read64LE(src, pos) : ReadPadded64(src, pos);
        if (minMatch == 5)
        {
            return (uint)((((value << 24) * Prime5) ^ salt) >> (64 - hBits));
        }

        if (minMatch == 6)
        {
            return (uint)((((value << 16) * Prime6) ^ salt) >> (64 - hBits));
        }

        if (minMatch == 7)
        {
            return (uint)((((value << 8) * Prime7) ^ salt) >> (64 - hBits));
        }

        return (uint)(((value * Prime8) ^ salt) >> (64 - hBits));
    }

    private static uint ReadPadded32(ReadOnlySpan<byte> src, int pos)
    {
        uint value = 0;
        for (var i = 0; i < 4 && pos + i < src.Length; i++)
        {
            value |= (uint)src[pos + i] << (8 * i);
        }

        return value;
    }

    private static ulong ReadPadded64(ReadOnlySpan<byte> src, int pos)
    {
        ulong value = 0;
        for (var i = 0; i < 8 && pos + i < src.Length; i++)
        {
            value |= (ulong)src[pos + i] << (8 * i);
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

    private static int Highbit32(uint value)
    {
        return 31 - BitOperations.LeadingZeroCount(value);
    }

    private static int WindowLow(int curr, int windowLog)
    {
        var window = 1 << windowLog;
        return curr > window ? curr - window : 0;
    }

    private static bool IsBinaryTree(ZstdStrategy strategy)
    {
        return strategy == ZstdStrategy.BtLazy2;
    }

    /// <summary>
    /// Parses <paramref name="source"/> exactly like native greedy/lazy/lazy2
    /// at <paramref name="level"/> and stores sequences into
    /// <paramref name="store"/>. <paramref name="repeatOffsets"/> is the
    /// frame-scoped history (init <c>{1,4,8}</c>); entries 0..1 are synced per
    /// the native end-of-block rule, entry 2 passes through (native lazy never
    /// touches it). Returns the trailing literal length.
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets, int level)
    {
        if (source.Length == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        // Tier row + ZSTD_adjustCParams_internal (no dict).
        var table = ZstdCompressionParameters.ForSizeAndLevel(source.Length, level).AdjustForSize(source.Length);
        return FindMatches(source, store, repeatOffsets, table);
    }

    /// <summary>
    /// Parses one frame block with an explicitly supplied (already adjusted)
    /// parameter row (see <see cref="ZstdMatchFinder.FindMatches(ReadOnlySpan{byte}, ZstdSequenceStore, uint[], ZstdCompressionParameters)"/> for why
    /// multi-block frames share the frame-level row).
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets,
        ZstdCompressionParameters table)
    {
        if (source.Length == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        uint[] hashTable = new uint[1 << table.HashLog];
        uint[] chainTable = new uint[1 << table.ChainLog];
        byte[] tagTable = new byte[1 << table.HashLog];
        var nextToUpdate = 0;
        return FindMatchesCore(
            source, 0, source.Length, hashTable, chainTable, tagTable,
            ref nextToUpdate, store, repeatOffsets, table);
    }

    /// <summary>
    /// Parses one frame block <c>[blockStart, blockEnd)</c> of the frame held
    /// by <paramref name="state"/>, with the frame-persistent tables and
    /// update cursor (see <see cref="ZstdFast.FindMatches(ZstdFrameState,int,int,ZstdSequenceStore,uint[])"/>
    /// for the positioning contract). The lazy-skipping flag resets per block
    /// like upstream (<c>ms-&gt;lazySkipping = 0</c> at block entry).
    /// </summary>
    internal static int FindMatches(
        ZstdFrameState state, int blockStart, int blockEnd,
        ZstdSequenceStore store, uint[] repeatOffsets)
    {
        ArgumentNullException.ThrowIfNull(state);
        var table = state.Prm;
        var useRow = table.Strategy is not ZstdStrategy.BtLazy2 && table.WindowLog > 14;
        uint[] hashTable, chainTable;
        byte[] tagTable;
        if (useRow)
        {
            (hashTable, tagTable) = state.LazyRowTables();
            chainTable = [];
        }
        else
        {
            (hashTable, chainTable) = state.LazyChainTables();
            tagTable = [];
        }

        return FindMatchesCore(
            state.Frame, blockStart, blockEnd, hashTable, chainTable, tagTable,
            ref state.NextToUpdate, store, repeatOffsets, table);
    }

    private static int FindMatchesCore(
        ReadOnlySpan<byte> source, int blockStart, int blockEnd,
        uint[] hashTable, uint[] chainTable, byte[] tagTable,
        ref int nextToUpdate,
        ZstdSequenceStore store, uint[] repeatOffsets, ZstdCompressionParameters table)
    {
        var windowLog = table.WindowLog;
        var chainLog = table.ChainLog;
        var hashLog = table.HashLog;
        var searchLog = table.SearchLog;
        var mls = Math.Clamp(table.MinMatch, 4, 6); // BOUNDED(4, minMatch, 6)
        var depth = table.Strategy switch
        {
            ZstdStrategy.Greedy => 0,
            ZstdStrategy.Lazy => 1,
            ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(table), $"Strategy {table.Strategy} is not greedy/lazy."),
        };
        var useRow = !IsBinaryTree(table.Strategy) && windowLog > 14; // ZSTD_resolveRowMatchFinderMode.
        var useBt = table.Strategy == ZstdStrategy.BtLazy2;
        var rowLog = Math.Clamp(searchLog, 4, 6);
        var rowHashLog = hashLog - rowLog;

        var ilimit = useRow ? blockEnd - 8 - RowHashCacheSize : blockEnd - 8;

        var offset1 = repeatOffsets[0];
        var offset2 = repeatOffsets[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        // Block-start rep invalidation (lowLimit 0 in practice; absolute
        // positions keep the same formula across blocks).
        // ip starts at 1 on the first block (dictAndPrefixLength == 0) and at
        // the block start afterwards (never equal to the frame start again).
        var curr0 = blockStart == 0 ? 1 : blockStart;
        var maxRep = curr0 - WindowLow(curr0, windowLog);
        if (offset2 > (uint)maxRep)
        {
            offsetSaved2 = offset2;
            offset2 = 0;
        }

        if (offset1 > (uint)maxRep)
        {
            offsetSaved1 = offset1;
            offset1 = 0;
        }

        var anchor = blockStart;
        var ip = blockStart == 0 ? 1 : blockStart;
        var lazySkipping = false; // Reset per block, like ms->lazySkipping.

        while (ip < ilimit)
        {
            var matchLength = 0;
            var offBase = ZstdSeq.Repcode1;
            var start = ip + 1;

            // Repcode probe at ip+1.
            if (offset1 > 0 && Read32LE(source, ip + 1 - (int)offset1) == Read32LE(source, ip + 1))
            {
                matchLength = 4 + CountMatches(source, ip + 5, ip + 5 - (int)offset1, blockEnd);
                if (depth == 0)
                {
                    goto StoreSequence;
                }
            }

            // First search (depth 0).
            {
                uint found = 999999999;
                var ml2 = useRow
                    ? RowFindBestMatch(source, blockEnd, ip, ref found, mls, rowHashLog, rowLog, searchLog,
                        windowLog, hashTable, tagTable, ref nextToUpdate, ref lazySkipping)
                    : useBt
                        ? ZstdBinaryTree.BtFindBestMatch(source, blockEnd, ip, ref found, mls, hashLog, searchLog,
                            chainLog, windowLog, hashTable, chainTable, ref nextToUpdate)
                        : HcFindBestMatch(source, blockEnd, ip, ref found, mls, hashLog, searchLog, chainLog,
                            windowLog, hashTable, chainTable, ref nextToUpdate, ref lazySkipping);
                if (ml2 > matchLength)
                {
                    matchLength = ml2;
                    start = ip;
                    offBase = found;
                }
            }

            if (matchLength < 4)
            {
                var step = ((ip - anchor) >> 8) + 1; // kSearchStrength
                ip += step;
                lazySkipping = step > 8; // kLazySkippingStep
                continue;
            }

            // Lazy evaluation (depth 1, plus depth 2 for lazy2).
            if (depth >= 1)
            {
                while (ip < ilimit)
                {
                    ip++;
                    if (offBase != 0 && offset1 > 0 && Read32LE(source, ip) == Read32LE(source, ip - (int)offset1))
                    {
                        var mlRep = 4 + CountMatches(source, ip + 4, ip + 4 - (int)offset1, blockEnd);
                        var gain2 = mlRep * 3;
                        var gain1 = (matchLength * 3) - Highbit32(offBase) + 1;
                        if (mlRep >= 4 && gain2 > gain1)
                        {
                            matchLength = mlRep;
                            offBase = ZstdSeq.Repcode1;
                            start = ip;
                        }
                    }

                    {
                        uint candidate = 999999999;
                        var ml2 = useRow
                            ? RowFindBestMatch(source, blockEnd, ip, ref candidate, mls, rowHashLog, rowLog, searchLog,
                                windowLog, hashTable, tagTable, ref nextToUpdate, ref lazySkipping)
                            : useBt
                                ? ZstdBinaryTree.BtFindBestMatch(source, blockEnd, ip, ref candidate, mls, hashLog, searchLog,
                                    chainLog, windowLog, hashTable, chainTable, ref nextToUpdate)
                                : HcFindBestMatch(source, blockEnd, ip, ref candidate, mls, hashLog, searchLog, chainLog,
                                    windowLog, hashTable, chainTable, ref nextToUpdate, ref lazySkipping);
                        var gain2 = (ml2 * 4) - Highbit32(candidate);
                        var gain1 = (matchLength * 4) - Highbit32(offBase) + 4;
                        if (ml2 >= 4 && gain2 > gain1)
                        {
                            matchLength = ml2;
                            offBase = candidate;
                            start = ip;
                            continue;
                        }
                    }

                    if (depth == 2 && ip < ilimit)
                    {
                        ip++;
                        if (offBase != 0 && offset1 > 0 && Read32LE(source, ip) == Read32LE(source, ip - (int)offset1))
                        {
                            var mlRep = 4 + CountMatches(source, ip + 4, ip + 4 - (int)offset1, blockEnd);
                            var gain2 = mlRep * 4;
                            var gain1 = (matchLength * 4) - Highbit32(offBase) + 1;
                            if (mlRep >= 4 && gain2 > gain1)
                            {
                                matchLength = mlRep;
                                offBase = ZstdSeq.Repcode1;
                                start = ip;
                            }
                        }

                        {
                            uint candidate = 999999999;
                            var ml2 = useRow
                                ? RowFindBestMatch(source, blockEnd, ip, ref candidate, mls, rowHashLog, rowLog, searchLog,
                                    windowLog, hashTable, tagTable, ref nextToUpdate, ref lazySkipping)
                                : useBt
                                    ? ZstdBinaryTree.BtFindBestMatch(source, blockEnd, ip, ref candidate, mls, hashLog, searchLog,
                                        chainLog, windowLog, hashTable, chainTable, ref nextToUpdate)
                                    : HcFindBestMatch(source, blockEnd, ip, ref candidate, mls, hashLog, searchLog, chainLog,
                                        windowLog, hashTable, chainTable, ref nextToUpdate, ref lazySkipping);
                            var gain2 = (ml2 * 4) - Highbit32(candidate);
                            var gain1 = (matchLength * 4) - Highbit32(offBase) + 7;
                            if (ml2 >= 4 && gain2 > gain1)
                            {
                                matchLength = ml2;
                                offBase = candidate;
                                start = ip;
                                continue;
                            }
                        }
                    }

                    break;
                }
            }

        StoreSequence:
            // Catch up: match may extend backwards while strictly above the prefix start.
            if (ZstdSeq.IsOffset(offBase))
            {
                var offset = (int)ZstdSeq.ToOffset(offBase);
                while (start > anchor && start - offset > 0 && source[start - 1] == source[start - offset - 1])
                {
                    start--;
                    matchLength++;
                }

                offset2 = offset1;
                offset1 = (uint)offset;
            }

            store.StoreSequence(source.Slice(anchor, start - anchor), offBase, matchLength);
            anchor = start + matchLength;
            ip = anchor;
            lazySkipping = false;

            // Immediate repcode (offset_2), ll=0 with swap.
            while (ip <= ilimit && offset2 > 0
                && Read32LE(source, ip) == Read32LE(source, ip - (int)offset2))
            {
                var repLength = 4 + CountMatches(source, ip + 4, ip + 4 - (int)offset2, blockEnd);
                var swap = offset2;
                offset2 = offset1;
                offset1 = swap;
                store.StoreSequence([], ZstdSeq.Repcode1, repLength);
                ip += repLength;
                anchor = ip;
            }
        }

        // End-of-block rep sync (ZSTD_compressBlock_lazy_generic tail).
        offsetSaved2 = (offsetSaved1 != 0 && offset1 != 0) ? offsetSaved1 : offsetSaved2;
        repeatOffsets[0] = offset1 != 0 ? offset1 : offsetSaved1;
        repeatOffsets[1] = offset2 != 0 ? offset2 : offsetSaved2;

        store.SetTrailingLiterals(source.Slice(anchor, blockEnd - anchor));
        return blockEnd - anchor;
    }

    /// <summary>
    /// <c>ZSTD_HcFindBestMatch</c>, noDict. Inserts <c>[nextToUpdate, ip)</c>
    /// (one position in lazy-skipping mode), walks at most
    /// <c>1 &lt;&lt; searchLog</c> chain candidates with the unguarded-style
    /// prefilter (guarded here: out-of-range candidates are skipped, matching
    /// native with probability 1 − 2⁻³²). Returns best length (≥ 3).
    /// </summary>
    private static int HcFindBestMatch(
        ReadOnlySpan<byte> src, int end, int ip, ref uint offBase,
        int mls, int hashLog, int searchLog, int chainLog, int windowLog,
        uint[] hashTable, uint[] chainTable,
        ref int nextToUpdate, ref bool lazySkipping)
    {
        var chainSize = 1 << chainLog;
        var chainMask = chainSize - 1;

        // ZSTD_insertAndFindFirstIndex_internal (prefix only).
        // Slots store pos+1 (0 = empty, mirroring ZSTD_WINDOW_START_INDEX).
        var idx = nextToUpdate;
        if (!lazySkipping)
        {
            while (idx < ip)
            {
                var h = HashSalted(src, idx, hashLog, mls, 0);
                chainTable[idx & chainMask] = hashTable[h];
                hashTable[h] = (uint)idx + 1;
                idx++;
            }
        }
        else if (idx < ip)
        {
            var h = HashSalted(src, idx, hashLog, mls, 0);
            chainTable[idx & chainMask] = hashTable[h];
            hashTable[h] = (uint)idx + 1;
        }

        nextToUpdate = ip;

        var lowLimit = WindowLow(ip, windowLog);
        var minChain = ip > chainSize ? ip - chainSize : 0;
        var attempts = 1 << searchLog;
        var best = 3; // ml = 4 - 1
        var matchIndex = (int)hashTable[HashSalted(src, ip, hashLog, mls, 0)] - 1;

        while (matchIndex >= lowLimit && attempts > 0)
        {
            var current = 0;
            // Prefilter reads 4 bytes at (match + best - 3); both must be
            // in range (native over-reads; see class remarks).
            if (matchIndex + best + 1 <= end && ip + best + 1 <= end
                && Read32LE(src, matchIndex + best - 3) == Read32LE(src, ip + best - 3))
            {
                current = CountMatches(src, ip, matchIndex, end);
            }

            if (current > best)
            {
                best = current;
                offBase = ZstdSeq.OffsetToOffBase((uint)(ip - matchIndex));
                if (ip + current == end)
                {
                    break;
                }
            }

            if (matchIndex <= minChain)
            {
                break;
            }

            matchIndex = (int)chainTable[matchIndex & chainMask] - 1; // slots store pos+1
            attempts--;
        }

        return best;
    }

    /// <summary>
    /// <c>ZSTD_row_nextIndex</c>: next insert position within a tag row
    /// (circular buffer cycling backwards, skipping position 0 which holds
    /// the head). Updates the head byte in place.
    /// </summary>
    private static uint RowNextIndex(byte[] tagTable, int rowStart, uint rowMask)
    {
        uint next = (uint)((tagTable[rowStart] - 1) & rowMask);
        if (next == 0)
        {
            next += rowMask;
        }

        tagTable[rowStart] = (byte)next;
        return next;
    }

    /// <summary>
    /// <c>ZSTD_row_update_internalImpl</c> over <c>[updateStartIdx, updateEndIdx)</c>:
    /// inserts each position into its salted-hash row. The hash cache is a
    /// memo only, so hashes are computed directly (identical values).
    /// </summary>
    private static void RowUpdateRange(
        ReadOnlySpan<byte> src, int updateStartIdx, int updateEndIdx,
        int mls, int rowHashLog, int rowLog, uint rowMask,
        uint[] hashTable, byte[] tagTable)
    {
        for (var idx = updateStartIdx; idx < updateEndIdx; idx++)
        {
            var hash = HashSalted(src, idx, rowHashLog + RowHashTagBits, mls, FreshHashSalt);
            var relRow = (int)((hash >> RowHashTagBits) << rowLog);
            var pos = RowNextIndex(tagTable, relRow, rowMask);
            tagTable[relRow + (int)pos] = (byte)(hash & RowHashTagMask);
            hashTable[relRow + (int)pos] = (uint)idx + 1; // +1: 0 means empty
        }
    }

    /// <summary>
    /// <c>ZSTD_RowFindBestMatch</c>, noDict (<c>dictMode</c> branches deleted).
    /// Updates rows up to <paramref name="ip"/> (with the 384/96/32 skip rule
    /// in lazy-skipping mode), collects tag-matching candidates in row order
    /// (head forward, position 0 skipped, lowLimit break, attempts capped at
    /// <c>min(searchLog,rowLog)</c>), inserts the current position, then
    /// returns the longest match (strictly greater wins ties).
    /// </summary>
    private static int RowFindBestMatch(
        ReadOnlySpan<byte> src, int end, int ip, ref uint offBase,
        int mls, int rowHashLog, int rowLog, int searchLog, int windowLog,
        uint[] hashTable, byte[] tagTable,
        ref int nextToUpdate, ref bool lazySkipping)
    {
        var rowEntries = 1 << rowLog;
        var rowMask = (uint)(rowEntries - 1);
        var cappedSearchLog = Math.Min(searchLog, rowLog);
        var attempts = 1 << cappedSearchLog;
        var best = 3; // ml = 4 - 1

        var lowLimit = WindowLow(ip, windowLog);

        // Update rows up to (not including) ip.
        if (!lazySkipping)
        {
            if (ip - nextToUpdate > 384)
            {
                RowUpdateRange(src, nextToUpdate, nextToUpdate + 96, mls, rowHashLog, rowLog, rowMask, hashTable, tagTable);
                nextToUpdate = ip - 32;
            }

            RowUpdateRange(src, nextToUpdate, ip, mls, rowHashLog, rowLog, rowMask, hashTable, tagTable);
            nextToUpdate = ip;
        }

        var hash = HashSalted(src, ip, rowHashLog + RowHashTagBits, mls, FreshHashSalt);
        if (lazySkipping)
        {
            nextToUpdate = ip;
        }

        var relRow = (int)((hash >> RowHashTagBits) << rowLog);
        var tag = (byte)(hash & RowHashTagMask);
        var head = (uint)(tagTable[relRow] & rowMask);

        // Collect candidates in row order from head forward.
        var matchBuffer = new uint[rowEntries];
        var numMatches = 0;
        for (var k = 0; k < rowEntries && attempts > 0; k++)
        {
            var matchPos = (int)((head + (uint)k) & rowMask);
            if (matchPos == 0)
            {
                continue;
            }

            if (tagTable[relRow + matchPos] != tag)
            {
                continue;
            }

            var matchIndex = (int)hashTable[relRow + matchPos] - 1; // slots store pos+1
            if (matchIndex < lowLimit)
            {
                break;
            }

            matchBuffer[numMatches++] = (uint)matchIndex;
            attempts--;
        }

        // Speed opt: insert current byte too (avoids one update iteration next search).
        {
            var pos = RowNextIndex(tagTable, relRow, rowMask);
            tagTable[relRow + (int)pos] = tag;
            hashTable[relRow + (int)pos] = (uint)(++nextToUpdate); // value pos+1, cursor advances
        }

        // Return the longest match.
        for (var m = 0; m < numMatches; m++)
        {
            var matchIndex = (int)matchBuffer[m];
            var current = 0;
            if (matchIndex + best + 1 <= end && ip + best + 1 <= end
                && Read32LE(src, matchIndex + best - 3) == Read32LE(src, ip + best - 3))
            {
                current = CountMatches(src, ip, matchIndex, end);
            }

            if (current > best)
            {
                best = current;
                offBase = ZstdSeq.OffsetToOffBase((uint)(ip - matchIndex));
                if (ip + current == end)
                {
                    break;
                }
            }
        }

        return best;
    }
}
