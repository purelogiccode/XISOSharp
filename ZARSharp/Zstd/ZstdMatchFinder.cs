using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// Match-finder parameters for one level, copied from the
/// <c>srcSize &lt;= 128 KiB</c> row of <c>lib/compress/clevels.h</c>
/// (columns W C H S L T = windowLog chainLog hashLog searchLog minMatch
/// targetLength). ZAR blocks are always 64 KiB so that row always applies.
/// </summary>
/// <param name="WindowLog">Window log (17; single-shot window covers the block).</param>
/// <param name="ChainLog">Hash-chain log (lazy only).</param>
/// <param name="HashLog">Hash table log.</param>
/// <param name="SearchLog">Chain search depth log (lazy only).</param>
/// <param name="MinMatch">Minimum match length used for hashing.</param>
/// <param name="TargetLength">Target match length (fast step size).</param>
/// <param name="Depth">Lazy depth: 0 = greedy, 1 = lazy (never 2 below level 7).</param>
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
    /// <summary>Parameters for <paramref name="level"/> (1..6).</summary>
    public static ZstdMatchParams ForLevel(int level) => level switch
    {
        1 => new(17, 12, 13, 1, 6, 0, 0, UseChain: false), // fast
        2 => new(17, 13, 15, 1, 5, 0, 0, UseChain: false), // fast (dfast mapped here)
        3 => new(17, 15, 16, 2, 5, 0, 0, UseChain: true), // greedy = lazy depth 0
        4 => new(17, 17, 17, 2, 4, 0, 1, UseChain: true), // lazy depth 1
        5 => new(17, 16, 17, 3, 4, 2, 1, UseChain: true), // lazy depth 1
        6 => new(17, 16, 17, 3, 4, 4, 1, UseChain: true), // lazy depth 1
        _ => throw new ArgumentOutOfRangeException(nameof(level), "Level must be 1..6."),
    };
}

/// <summary>
/// zstd match finder for single-shot blocks (no dictionaries, window starts
/// empty, all matches bounded by the current block). Ports, in order:
/// <list type="number">
/// <item><b>Fast</b> (levels 1–2): <c>lib/compress/zstd_fast.c</c>
/// (<c>ZSTD_compressBlock_fast_noDict_generic</c>) with the hash table sized by
/// <c>hashLog</c> and no chains.</item>
/// <item><b>Greedy</b> (level 3): <c>lib/compress/zstd_lazy.c</c>
/// <c>ZSTD_compressBlock_lazy_generic</c> with depth 0.</item>
/// <item><b>Lazy</b> (levels 4–6): same with depth 1, hash chain of depth
/// <c>searchLog</c>, no binary tree.</item>
/// </list>
/// Level 2 (double-fast upstream) maps to fast, per the port plan.
/// Deliberate deviations from upstream (validity-preserving, ratio-neutral):
/// <list type="bullet">
/// <item>The fast pipelined pair-interleaved loop is a plain sequential pair
/// loop with identical pair coverage (pairs spaced by <c>step</c>) and the same
/// step-acceleration schedule; repcode is probed at the pair positions instead
/// of ahead.</item>
/// <item>Backward match extension ("catch up") is full-length on every path;
/// history stays decoder-consistent because every stored sequence updates the
/// 3-entry repeat history via <see cref="ZstdSeq.UpdateRep"/> (upstream relies
/// on the fast rep path always having non-zero literal length instead).</item>
/// <item>Repeat history is kept decoder-synchronous at all times (true values,
/// updated per stored sequence) instead of upstream's block-start zeroing plus
/// end-of-block restore; the per-probe range guards subsume the invalidation
/// (an "invalid" offset simply fails its guard until enough bytes exist, which
/// is exactly when the decoder would accept it too).</item>
/// <item>Row-hash and binary-tree search are not ported (hash chains only).</item>
/// <item><c>ZSTD_hashPtr</c> reads are zero-padded at the tail: upstream
/// over-reads up to 7 bytes past the search limit (safe in C, an exception in
/// C#). Padded hashes only affect which matches are <em>found</em>; every
/// candidate is still verified with in-bounds 4-byte compares and a bounded
/// <c>ZSTD_count</c>, so output stays valid.</item>
/// </list>
/// </summary>
public sealed class ZstdMatchFinder
{
    // Prime constants from lib/compress/zstd_compress_internal.h:898-926.
    private const uint Prime4Bytes = 2654435761U;
    private const ulong Prime5Bytes = 889523592379UL;
    private const ulong Prime6Bytes = 227718039650203UL;
    private const ulong Prime7Bytes = 58295818150454627UL;
    private const ulong Prime8Bytes = 0xCF1BBCDCB7A56463UL;

    private const int SearchStrength = 8; // kSearchStrength
    private const int StepIncrement = 1 << (SearchStrength - 1); // kStepIncr = 128
    private const int LazySkippingStep = 8; // kLazySkippingStep

    private readonly ZstdMatchParams _params;
    private readonly int _level;
    private readonly uint[] _hashTable;
    private readonly uint[] _chainTable; // Empty unless UseChain.

    /// <summary>Creates a finder for <paramref name="level"/> (1..6).</summary>
    public ZstdMatchFinder(int level)
    {
        _params = ZstdMatchParams.ForLevel(level);
        _level = level;
        _hashTable = new uint[1 << _params.HashLog];
        _chainTable = _params.UseChain ? new uint[1 << _params.ChainLog] : [];
    }

    /// <summary>Compression level (1..6).</summary>
    public int Level => _level;

    /// <summary>Effective strategy (1→Fast, 2→DoubleFast-as-fast, 3→Greedy, 4–6→Lazy).</summary>
    public ZstdStrategy Strategy => (ZstdStrategy)_level;

    /// <summary>Parameters in effect.</summary>
    public ZstdMatchParams Params => _params;

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

        Array.Clear(_hashTable);
        if (_chainTable.Length > 0)
        {
            Array.Clear(_chainTable);
        }

        return _params.UseChain
            ? FindLazy(source, store, repeatOffsets)
            : FindFast(source, store, repeatOffsets);
    }

    // ------------------------------------------------------------------
    // Hashing (ZSTD_hashPtr) and match counting (ZSTD_count)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>ZSTD_hashPtr(p, hBits, mls)</c>. Reads up to 8 bytes little-endian at
    /// <paramref name="pos"/>, zero-padded past the end (see class remarks).
    /// </summary>
    internal static uint HashPtr(ReadOnlySpan<byte> src, int pos, int hashLog, int minMatch)
    {
        ulong value = Read64Padded(src, pos);
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
        int available = src.Length - pos;
        if (available > 7)
        {
            available = 8;
        }

        for (int i = 0; i < available; i++)
        {
            value |= (ulong)src[pos + i] << (8 * i);
        }

        return value;
    }

    private static uint Read32(ReadOnlySpan<byte> src, int pos) =>
        (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));

    /// <summary>
    /// <c>ZSTD_count</c>: counts matching bytes of <c>src[ip..]</c> vs
    /// <c>src[match..]</c>, bounded by <paramref name="end"/>. The match side
    /// is always in range because <c>match &lt; ip</c>.
    /// </summary>
    private static int CountMatches(ReadOnlySpan<byte> src, int ip, int match, int end)
    {
        int start = ip;
        while (ip < end && src[ip] == src[match])
        {
            ip++;
            match++;
        }

        return ip - start;
    }

    /// <summary><c>ZSTD_highbit32</c>: index of the highest set bit (input ≥ 1).</summary>
    private static int Highbit32(uint value) => 31 - BitOperations.LeadingZeroCount(value);

    private int WindowLow(int curr) => WindowLowFor(curr, _params.WindowLog);

    private static int WindowLowFor(int curr, int windowLog)
    {
        int window = 1 << windowLog;
        return curr > window ? curr - window : 0;
    }

    // ------------------------------------------------------------------
    // Fast (zstd_fast.c, noDict)
    // ------------------------------------------------------------------

    private int FindFast(ReadOnlySpan<byte> src, ZstdSequenceStore store, uint[] rep)
    {
        int n = src.Length;
        if (n == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        int hashLog = _params.HashLog;
        int mls = _params.MinMatch;
        int stepSize = _params.TargetLength + (_params.TargetLength == 0 ? 1 : 0) + 1; // min 2

        // Decoder-synchronous repeat history: true values at all times,
        // evolved with UpdateRep after every stored sequence (see class remarks).
        uint[] history = [rep[0], rep[1], rep[2]];
        uint offset1 = history[0];
        uint offset2 = history[1];

        int anchor = 0;
        int ip = 1; // ip0 += (ip0 == prefixStart): position 0 has no history.
        int ilimit = n - 8;

        int step = stepSize;
        int nextStep = ip + StepIncrement;

        while (ip + 1 <= ilimit)
        {
            bool matched = false;
            for (int k = 0; k < 2 && !matched; k++)
            {
                int pos = ip + k;

                // Repcode probe (guarded; upstream relies on the invariant).
                if (offset1 > 0 && offset1 <= (uint)(pos - WindowLow(pos))
                                && Read32(src, pos) == Read32(src, pos - (int)offset1))
                {
                    int start = pos;
                    int match = pos - (int)offset1;
                    int length = 4;
                    while (start > anchor && match > WindowLow(start) && src[start - 1] == src[match - 1])
                    {
                        start--;
                        match--;
                        length++;
                    }

                    length += CountMatches(src, start + length, match + length, n);
                    history[0] = offset1;
                    history[1] = offset2;
                    store.StoreSequence(src.Slice(anchor, start - anchor), ZstdSeq.Repcode1, length);
                    ZstdSeq.UpdateRep(history, ZstdSeq.Repcode1, start == anchor ? 1u : 0u);
                    offset1 = history[0];
                    offset2 = history[1];
                    anchor = start + length;
                    ip = anchor;
                    PostMatchFill(src, _hashTable, hashLog, mls, n, ilimit, start, ip);
                    ip = ImmediateRepLoop(src, store, history, n, ilimit, ip, _params.WindowLog, ref anchor);
                    offset1 = history[0];
                    offset2 = history[1];
                    step = stepSize;
                    nextStep = ip + StepIncrement;
                    matched = true;
                    break;
                }

                // Hash probe.
                uint h = HashPtr(src, pos, hashLog, mls);
                int index = (int)_hashTable[h];
                _hashTable[h] = (uint)pos;
                if (index < pos && index >= WindowLow(pos) && Read32(src, pos) == Read32(src, index))
                {
                    int start = pos;
                    int match = index;
                    int length = 4;
                    while (start > anchor && match > WindowLow(start) && src[start - 1] == src[match - 1])
                    {
                        start--;
                        match--;
                        length++;
                    }

                    length += CountMatches(src, start + length, match + length, n);
                    uint offset = (uint)(start - match);
                    history[0] = offset1;
                    history[1] = offset2;
                    uint offBase = ZstdSeq.OffsetToOffBase(offset);
                    store.StoreSequence(src.Slice(anchor, start - anchor), offBase, length);
                    ZstdSeq.UpdateRep(history, offBase, start == anchor ? 1u : 0u);
                    offset1 = history[0];
                    offset2 = history[1];
                    anchor = start + length;
                    ip = anchor;
                    PostMatchFill(src, _hashTable, hashLog, mls, n, ilimit, start, ip);
                    ip = ImmediateRepLoop(src, store, history, n, ilimit, ip, _params.WindowLog, ref anchor);
                    offset1 = history[0];
                    offset2 = history[1];
                    step = stepSize;
                    nextStep = ip + StepIncrement;
                    matched = true;
                }
            }

            if (!matched)
            {
                if (ip + step >= nextStep)
                {
                    step++;
                    nextStep += StepIncrement;
                }

                ip += step;
            }
        }

        // Save reps for the next block (decoder-synchronous already).
        rep[0] = history[0];
        rep[1] = history[1];
        rep[2] = history[2];

        store.SetTrailingLiterals(src.Slice(anchor, n - anchor));
        return n - anchor;
    }

    /// <summary>
    /// Post-match table fill: hash the inside-match position and the resume
    /// area when hashable (upstream fills <c>current0+2</c> and <c>ip0-2</c>).
    /// </summary>
    private static void PostMatchFill(
        ReadOnlySpan<byte> src, uint[] hashTable, int hashLog, int mls,
        int n, int ilimit, int matchStart, int matchEnd)
    {
        if (matchStart + 2 <= ilimit)
        {
            hashTable[HashPtr(src, matchStart + 2, hashLog, mls)] = (uint)(matchStart + 2);
        }

        if (matchEnd - 2 >= 0 && matchEnd - 2 <= ilimit && matchEnd - 2 < n)
        {
            hashTable[HashPtr(src, matchEnd - 2, hashLog, mls)] = (uint)(matchEnd - 2);
        }

        if (matchEnd - 1 >= 0 && matchEnd - 1 <= ilimit && matchEnd - 1 < n)
        {
            hashTable[HashPtr(src, matchEnd - 1, hashLog, mls)] = (uint)(matchEnd - 1);
        }
    }

    /// <summary>
    /// Immediate-repcode loop after a match: while the bytes at <c>ip</c>
    /// repeat at <c>offset_2</c>, store zero-literal repcode-1 sequences
    /// (upstream swaps <c>rep_offset2 &lt;=&gt; rep_offset1</c>; here
    /// <see cref="ZstdSeq.UpdateRep"/> with <c>ll0=1</c> does the same).
    /// Returns the advanced position.
    /// </summary>
    private static int ImmediateRepLoop(
        ReadOnlySpan<byte> src, ZstdSequenceStore store, uint[] history,
        int n, int ilimit, int ip, int windowLog, ref int anchor)
    {
        // NOTE: no hash-table writes here. The lazy chain catch-up inserts
        // every visited position exactly once; writing hashTable[hash(ip)] = ip
        // early would let a later catch-up store chain[ip] = ip (self-loop).
        while (ip <= ilimit && history[1] > 0 && history[1] <= (uint)(ip - WindowLowFor(ip, windowLog))
               && Read32(src, ip) == Read32(src, ip - (int)history[1]))
        {
            int length = 4 + CountMatches(src, ip + 4, ip + 4 - (int)history[1], n);
            store.StoreSequence([], ZstdSeq.Repcode1, length);
            ZstdSeq.UpdateRep(history, ZstdSeq.Repcode1, 1u);
            ip += length;
            anchor = ip;
        }

        return ip;
    }

    // ------------------------------------------------------------------
    // Lazy / greedy (zstd_lazy.c, noDict, hash chain)
    // ------------------------------------------------------------------

    private int FindLazy(ReadOnlySpan<byte> src, ZstdSequenceStore store, uint[] rep)
    {
        int n = src.Length;
        if (n == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        int hashLog = _params.HashLog;
        int mls = Math.Clamp(_params.MinMatch, 4, 6); // BOUNDED(4, minMatch, 6)
        int searchLog = _params.SearchLog;
        int depth = _params.Depth;
        int chainSize = 1 << _params.ChainLog;
        int chainMask = chainSize - 1;

        uint[] history = [rep[0], rep[1], rep[2]];
        uint offset1 = history[0];
        uint offset2 = history[1];

        int anchor = 0;
        int ip = 1; // ip += (dictAndPrefixLength == 0)
        int ilimit = n - 8;

        int nextToUpdate = 0;
        bool lazySkipping = false;

        while (ip < ilimit)
        {
            int matchLength = 0;
            uint offBase = ZstdSeq.Repcode1;
            int start = ip + 1;

            // Repcode probe at ip+1 (upstream checks rep at the next position).
            if (offset1 > 0 && offset1 <= (uint)(ip + 1 - WindowLow(ip + 1))
                            && Read32(src, ip + 1) == Read32(src, ip + 1 - (int)offset1))
            {
                matchLength = 4 + CountMatches(src, ip + 5, ip + 5 - (int)offset1, n);
                if (depth == 0)
                {
                    goto StoreSequence;
                }
            }

            // First search (depth 0).
            {
                uint found = 999999999;
                int ml2 = HcFindBestMatch(src, ip, n, ref found, hashLog, mls, searchLog, chainMask, chainSize,
                    ref nextToUpdate, ref lazySkipping);
                if (ml2 > matchLength)
                {
                    matchLength = ml2;
                    start = ip;
                    offBase = found;
                }
            }

            if (matchLength < 4)
            {
                // Jump faster over incompressible sections.
                int skip = ((ip - anchor) >> SearchStrength) + 1;
                ip += skip;
                lazySkipping = skip > LazySkippingStep;
                continue;
            }

            // Lazy evaluation: look for a better match one (depth 1: two) ahead.
            if (depth >= 1)
            {
                while (ip < ilimit)
                {
                    ip++;
                    if (offBase != 0 && offset1 > 0 && offset1 <= (uint)(ip - WindowLow(ip))
                        && Read32(src, ip) == Read32(src, ip - (int)offset1))
                    {
                        int mlRep = 4 + CountMatches(src, ip + 4, ip + 4 - (int)offset1, n);
                        int gain2 = mlRep * 3;
                        int gain1 = matchLength * 3 - Highbit32(offBase) + 1;
                        if (mlRep >= 4 && gain2 > gain1)
                        {
                            matchLength = mlRep;
                            offBase = ZstdSeq.Repcode1;
                            start = ip;
                        }
                    }

                    {
                        uint candidate = 999999999;
                        int ml2 = HcFindBestMatch(src, ip, n, ref candidate, hashLog, mls, searchLog, chainMask,
                            chainSize, ref nextToUpdate, ref lazySkipping);
                        int gain2 = (ml2 * 4) - Highbit32(candidate);
                        int gain1 = (matchLength * 4) - Highbit32(offBase) + 4;
                        if (ml2 >= 4 && gain2 > gain1)
                        {
                            matchLength = ml2;
                            offBase = candidate;
                            start = ip;
                            continue;
                        }
                    }

                    break; // Depth 1 here (depth 2 only exists at level 7+).
                }
            }

            StoreSequence:
            // Catch up (offsets only; repcode matches need none).
            if (ZstdSeq.IsOffset(offBase))
            {
                uint offset = ZstdSeq.ToOffset(offBase);
                while (start > anchor && start - (int)offset > WindowLow(start)
                                      && src[start - 1] == src[start - (int)offset - 1])
                {
                    start--;
                    matchLength++;
                }
            }

            history[0] = offset1;
            history[1] = offset2;
            store.StoreSequence(src.Slice(anchor, start - anchor), offBase, matchLength);
            ZstdSeq.UpdateRep(history, offBase, start == anchor ? 1u : 0u);
            offset1 = history[0];
            offset2 = history[1];
            anchor = start + matchLength;
            ip = anchor;
            lazySkipping = false;

            // Immediate repcode (offset_2).
            while (ip <= ilimit && offset2 > 0 && offset2 <= (uint)(ip - WindowLow(ip))
                   && Read32(src, ip) == Read32(src, ip - (int)offset2))
            {
                int repLength = 4 + CountMatches(src, ip + 4, ip + 4 - (int)offset2, n);
                history[0] = offset1;
                history[1] = offset2;
                store.StoreSequence([], ZstdSeq.Repcode1, repLength);
                ZstdSeq.UpdateRep(history, ZstdSeq.Repcode1, 1u);
                offset1 = history[0];
                offset2 = history[1];
                ip += repLength;
                anchor = ip;
            }
        }

        rep[0] = history[0];
        rep[1] = history[1];
        rep[2] = history[2];

        store.SetTrailingLiterals(src.Slice(anchor, n - anchor));
        return n - anchor;
    }

    /// <summary>
    /// <c>ZSTD_HcFindBestMatch</c> (noDict): inserts positions up to
    /// <paramref name="ip"/> into the hash chain, then walks at most
    /// <c>1 &lt;&lt; searchLog</c> candidates. Returns the best match length
    /// (≥ 3 when nothing better is found) and sets
    /// <paramref name="offBase"/> to its offset code.
    /// </summary>
    private int HcFindBestMatch(
        ReadOnlySpan<byte> src, int ip, int end, ref uint offBase,
        int hashLog, int mls, int searchLog, int chainMask, int chainSize,
        ref int nextToUpdate, ref bool lazySkipping)
    {
        // ZSTD_insertAndFindFirstIndex_internal (prefix only).
        int idx = nextToUpdate;
        if (!lazySkipping)
        {
            while (idx < ip)
            {
                uint h = HashPtr(src, idx, hashLog, mls);
                _chainTable[idx & chainMask] = _hashTable[h];
                _hashTable[h] = (uint)idx;
                idx++;
            }
        }
        else
        {
            // Lazy-skipping mode: insert a single stale position (upstream).
            if (idx < ip)
            {
                uint h = HashPtr(src, idx, hashLog, mls);
                _chainTable[idx & chainMask] = _hashTable[h];
                _hashTable[h] = (uint)idx;
            }
        }

        nextToUpdate = ip;

        int lowLimit = WindowLow(ip);
        int minChain = ip > chainSize ? ip - chainSize : 0;
        int attempts = 1 << searchLog;
        int best = 3; // ml = 4 - 1
        int matchIndex = (int)_hashTable[HashPtr(src, ip, hashLog, mls)];

        while (matchIndex >= lowLimit && matchIndex < ip && attempts > 0)
        {
            int current = 0;
            // Prefilter: read the 4 bytes ending at the current best length.
            // Guarded so both reads stay in bounds (upstream over-reads here).
            if (matchIndex + best + 1 <= end && ip + best + 1 <= end
                                             && Read32(src, matchIndex + best - 3) == Read32(src, ip + best - 3))
            {
                current = CountMatches(src, ip, matchIndex, end);
            }
            else if (matchIndex + best + 1 > end || ip + best + 1 > end)
            {
                current = CountMatches(src, ip, matchIndex, end);
            }

            if (current > best)
            {
                best = current;
                offBase = ZstdSeq.OffsetToOffBase((uint)(ip - matchIndex));
                if (ip + current == end)
                {
                    break; // Best possible.
                }
            }

            if (matchIndex <= minChain)
            {
                break;
            }

            matchIndex = (int)_chainTable[matchIndex & chainMask];
            attempts--;
        }

        return best;
    }
}