namespace ZARSharp.Zstd;

/// <summary>
/// Exact C# port of <c>ZSTD_compressBlock_doubleFast_noDict_generic</c>
/// (<c>lib/compress/zstd_double_fast.c</c>, zstd-1.5.7) for fresh single-shot
/// blocks: no dictionaries, window starts empty, all matches bounded by the
/// block. Covers the <see cref="ZstdStrategy.DoubleFast"/> tier rows
/// (levels 3–4 at the ≤128 KiB tiers, plus any double-fast row). The long
/// table uses <c>hashLog</c> with <c>mls 8</c>; the small table reuses the
/// chain-table memory with <c>chainLog</c> and the row's <c>minMatch</c>.
/// Parameters come from the size-tier row plus
/// <see cref="ZstdCompressionParameters.AdjustForSize"/>.
/// <para/>
/// Faithful details: repcode probed at <c>ip+1</c>, 8-byte long-match probe
/// with the strict-greater recheck at <c>ip1</c>, short-match fallback with
/// long-at-next-position comparison, <c>step &lt; 4</c> guard for the deferred
/// <c>hl1</c> write-back, complementary insertion (<c>curr+2</c>,
/// <c>ip-2</c>/<c>ip-1</c> across both tables), block-start rep invalidation
/// with <c>offsetSaved</c> restore, and the immediate-repcode loop filling
/// both tables per iteration.
/// Deliberate guards (behavior-neutral with probability 1 − 2⁻³²): native
/// reads before the block start / past the block end (repcode lookbehind,
/// tail hashes) are treated as mismatches / zero-padded instead of reading
/// heap garbage; every stored candidate is verified in bounds.
/// Index discipline: table slots store <c>pos + 1</c> (never bare <c>pos</c>),
/// mirroring upstream where absolute indices start at
/// <c>ZSTD_WINDOW_START_INDEX (2)</c> and slot value 0 means "empty".
/// </summary>
internal static class ZstdDoubleFast
{
    private const int StepIncrement = 256; // kStepIncr = 1 << kSearchStrength

    private static uint Read32(ReadOnlySpan<byte> src, int pos)
    {
        return (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));
    }

    private static ulong Read64(ReadOnlySpan<byte> src, int pos)
    {
        return (ulong)Read32(src, pos) | ((ulong)Read32(src, pos + 4) << 32);
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

    private static int WindowLow(int curr, int windowLog)
    {
        var window = 1 << windowLog;
        return curr > window ? curr - window : 0;
    }

    /// <summary>
    /// Parses <paramref name="source"/> exactly like native double-fast and
    /// stores sequences into <paramref name="store"/>.
    /// <paramref name="repeatOffsets"/> is the frame-scoped history (init
    /// <c>{1,4,8}</c>), synced per the native end-of-block rule.
    /// <paramref name="prm"/> is the tier row already run through
    /// <see cref="ZstdCompressionParameters.AdjustForSize"/>.
    /// Returns the trailing literal length.
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets,
        ZstdCompressionParameters prm)
    {
        var n = source.Length;
        if (n == 0)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        var hBitsL = prm.HashLog;
        var hBitsS = prm.ChainLog;
        var mls = prm.MinMatch;
        var windowLog = prm.WindowLog;
        var hashLong = new uint[1 << hBitsL];
        var hashSmall = new uint[1 << hBitsS];
        var ilimit = n - 8;

        var offset1 = repeatOffsets[0];
        var offset2 = repeatOffsets[1];
        uint saved1 = 0, saved2 = 0;

        var anchor = 0;
        var ip = 0;
        if (ip == 0)
        {
            ip++; // ip == prefixLowest (fresh window starts at 0)
        }

        {
            var maxRep = ip - WindowLow(ip, windowLog);
            if (offset2 > (uint)maxRep)
            {
                saved2 = offset2;
                offset2 = 0;
            }

            if (offset1 > (uint)maxRep)
            {
                saved1 = offset1;
                offset1 = 0;
            }
        }

        while (true)
        {
            // Outer loop: one iteration per stored match.
            var step = 1;
            var nextStep = ip + StepIncrement;
            var ip1 = ip + step;
            if (ip1 > ilimit)
            {
                break; // _cleanup
            }

            var hl0 = ZstdMatchFinder.HashPtr(source, ip, hBitsL, 8);
            var idxl0 = (int)hashLong[hl0] - 1; // empty slot reads -1 (invalid)
            var stored = false;
            var lastLen = 0;
            var searchIp = ip; // curr of the finding iteration (insertion base)

            // Inner loop: one iteration per searched position.
            while (true)
            {
                var hs0 = ZstdMatchFinder.HashPtr(source, ip, hBitsS, mls);
                var idxs0 = (int)hashSmall[hs0] - 1; // empty slot reads -1 (invalid)
                searchIp = ip;

                hashLong[hl0] = hashSmall[hs0] = (uint)(ip + 1); // +1: 0 means empty

                // Repcode at ip+1.
                if (offset1 > 0 && ip + 1 - (int)offset1 >= 0
                    && Read32(source, ip + 1 - (int)offset1) == Read32(source, ip + 1))
                {
                    lastLen = 4 + CountMatches(source, ip + 1 + 4, ip + 1 + 4 - (int)offset1, n);
                    ip++;
                    store.StoreSequence(source.Slice(anchor, ip - anchor), ZstdSeq.Repcode1, lastLen);
                    stored = true;
                    break;
                }

                var hl1 = ZstdMatchFinder.HashPtr(source, ip1, hBitsL, 8);

                // Long match at ip (empty slots read -1 and cannot match).
                int foundIp, foundLen;
                uint foundOff;
                if (idxl0 >= 0 && Read64(source, idxl0) == Read64(source, ip))
                {
                    foundLen = 8 + CountMatches(source, ip + 8, idxl0 + 8, n);
                    foundOff = (uint)(ip - idxl0);
                    foundIp = ip;
                    var back = idxl0;
                    while (foundIp > anchor && back > 0 && source[foundIp - 1] == source[back - 1])
                    {
                        foundIp--;
                        back--;
                        foundLen++;
                    }
                }
                else
                {
                    var idxl1 = (int)hashLong[hl1] - 1; // empty slot reads -1 (invalid)

                    // Short match at ip?
                    if (idxs0 < 0 || Read32(source, idxs0) != Read32(source, ip))
                    {
                        if (ip1 >= nextStep)
                        {
                            step++;
                            nextStep += StepIncrement;
                        }

                        ip = ip1;
                        ip1 += step;

                        hl0 = hl1;
                        idxl0 = idxl1;

                        if (ip1 > ilimit)
                        {
                            break;
                        }

                        continue;
                    }

                    // _search_next_long: short match, maybe a longer one at ip1.
                    foundLen = 4 + CountMatches(source, ip + 4, idxs0 + 4, n);
                    foundOff = (uint)(ip - idxs0);
                    foundIp = ip;
                    var back = idxs0;

                    if (idxl1 > 0 && Read64(source, idxl1) == Read64(source, ip1))
                    {
                        var l1len = 8 + CountMatches(source, ip1 + 8, idxl1 + 8, n);
                        if (l1len > foundLen)
                        {
                            foundIp = ip1;
                            foundLen = l1len;
                            foundOff = (uint)(foundIp - idxl1);
                            back = idxl1;
                        }
                    }

                    while (foundIp > anchor && back > 0 && source[foundIp - 1] == source[back - 1])
                    {
                        foundIp--;
                        back--;
                        foundLen++;
                    }
                }

                // _match_found.
                offset2 = offset1;
                offset1 = foundOff;
                if (step < 4)
                {
                    hashLong[hl1] = (uint)ip1 + 1; // +1: 0 means empty
                }

                store.StoreSequence(
                    source.Slice(anchor, foundIp - anchor),
                    ZstdSeq.OffsetToOffBase(foundOff), foundLen);
                ip = foundIp;
                lastLen = foundLen;
                stored = true;
                break;
            }

            if (!stored)
            {
                break; // _cleanup
            }

            // _match_stored.
            ip += lastLen;
            anchor = ip;

            if (ip <= ilimit)
            {
                // Complementary insertion (guarded tail hashes, see class remarks).
                var insert = searchIp + 2;
                hashLong[ZstdMatchFinder.HashPtr(source, insert, hBitsL, 8)] = (uint)insert + 1;
                hashLong[ZstdMatchFinder.HashPtr(source, ip - 2, hBitsL, 8)] = (uint)(ip - 2) + 1;
                hashSmall[ZstdMatchFinder.HashPtr(source, insert, hBitsS, mls)] = (uint)insert + 1;
                hashSmall[ZstdMatchFinder.HashPtr(source, ip - 1, hBitsS, mls)] = (uint)(ip - 1) + 1;

                // Immediate repcode.
                while (ip <= ilimit && offset2 > 0 && ip - (int)offset2 >= 0
                    && Read32(source, ip) == Read32(source, ip - (int)offset2))
                {
                    var repLen = 4 + CountMatches(source, ip + 4, ip + 4 - (int)offset2, n);
                    (offset2, offset1) = (offset1, offset2);
                    hashSmall[ZstdMatchFinder.HashPtr(source, ip, hBitsS, mls)] = (uint)ip + 1;
                    hashLong[ZstdMatchFinder.HashPtr(source, ip, hBitsL, 8)] = (uint)ip + 1;
                    store.StoreSequence([], ZstdSeq.Repcode1, repLen);
                    ip += repLen;
                    anchor = ip;
                }
            }
        }

        saved2 = saved1 != 0 && offset1 != 0 ? saved1 : saved2;
        repeatOffsets[0] = offset1 != 0 ? offset1 : saved1;
        repeatOffsets[1] = offset2 != 0 ? offset2 : saved2;
        store.SetTrailingLiterals(source.Slice(anchor, n - anchor));
        return n - anchor;
    }
}
