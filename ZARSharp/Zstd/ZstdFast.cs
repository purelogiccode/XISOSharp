namespace ZARSharp.Zstd;

/// <summary>
/// Exact C# port of <c>ZSTD_compressBlock_fast_noDict_generic</c>
/// (<c>lib/compress/zstd_fast.c</c>, zstd-1.5.7) for fresh single-shot blocks:
/// no dictionaries, window starts empty, all matches bounded by the block.
/// Covers the <see cref="ZstdStrategy.Fast"/> tier rows (levels 1–2 at every
/// size tier). Parameters (hashLog, minMatch as <c>mls</c>, targetLength step)
/// come from the size-tier row plus
/// <see cref="ZstdCompressionParameters.AdjustForSize"/>.
/// <para/>
/// Faithful details: pair-interleaved search loop (repcode probed at
/// <c>ip2</c>, ahead of the hash probes), block-start rep invalidation with
/// <c>offsetSaved</c> restore, <c>stepSize = targetLength + !targetLength + 1</c>
/// with the 128-gap acceleration schedule, backward catch-up strictly above
/// the block start, post-match fills (<c>current0+2</c>, <c>ip0-2</c>), and the
/// immediate-repcode loop with per-iteration hash inserts.
/// <c>useCmov</c> needs no port: both match predicates compute the same
/// boolean (4-byte equality and index in range).
/// Deliberate guards (behavior-neutral with probability 1 − 2⁻³²): native
/// over-reads past the block end (hash prefilter, repcode lookbehind before
/// the block start) are treated as mismatches instead of reading heap
/// garbage; every stored candidate is verified in bounds.
/// Index discipline: table slots store <c>pos + 1</c> (never bare <c>pos</c>),
/// mirroring upstream where absolute indices start at
/// <c>ZSTD_WINDOW_START_INDEX (2)</c> and slot value 0 means "empty". A bare
/// 0-based store would conflate "empty" with "position 0" and take phantom
/// matches against the first four bytes.
/// </summary>
internal static class ZstdFast
{
    private const int StepIncrement = 128; // kStepIncr = 1 << (kSearchStrength-1)

    private static uint Read32(ReadOnlySpan<byte> src, int pos)
    {
        return (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24));
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
    /// Parses <paramref name="source"/> exactly like native fast and stores
    /// sequences into <paramref name="store"/>. <paramref name="repeatOffsets"/>
    /// is the frame-scoped history (init <c>{1,4,8}</c>), synced per the native
    /// end-of-block rule. <paramref name="prm"/> is the tier row already run
    /// through <see cref="ZstdCompressionParameters.AdjustForSize"/>.
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

        var hashLog = prm.HashLog;
        var mls = prm.MinMatch;
        var windowLog = prm.WindowLog;
        var stepSize = prm.TargetLength + (prm.TargetLength == 0 ? 1 : 0) + 1; // min 2
        var hashTable = new uint[1 << hashLog];
        var ilimit = n - 8;

        var rep1 = repeatOffsets[0];
        var rep2 = repeatOffsets[1];
        uint saved1 = 0, saved2 = 0;

        var anchor = 0;
        var ip0 = 0;
        if (ip0 == 0)
        {
            ip0++; // ip0 == prefixStart (fresh window starts at 0)
        }

        {
            var maxRep = ip0 - WindowLow(ip0, windowLog);
            if (rep2 > (uint)maxRep)
            {
                saved2 = rep2;
                rep2 = 0;
            }

            if (rep1 > (uint)maxRep)
            {
                saved1 = rep1;
                rep1 = 0;
            }
        }

        var step = stepSize;
        var nextStep = ip0 + StepIncrement;
        var ip1 = 0;
        var ip2 = 0;
        var ip3 = 0;
        var hash0 = 0u;
        var hash1 = 0u;
        var matchIdx = 0;

        while (true)
        {
            // _start: recompute the pair window after every stored match.
            step = stepSize;
            nextStep = ip0 + StepIncrement;
            ip1 = ip0 + 1;
            ip2 = ip0 + step;
            ip3 = ip2 + 1;
            if (ip3 >= ilimit)
            {
                break; // _cleanup
            }

            hash0 = ZstdMatchFinder.HashPtr(source, ip0, hashLog, mls);
            hash1 = ZstdMatchFinder.HashPtr(source, ip1, hashLog, mls);
            matchIdx = (int)hashTable[hash0] - 1; // empty slot reads -1 (invalid)
            var matched = false;

            while (true)
            {
                // Repcode at ip2 (upstream reads ip2-rep unconditionally;
                // out-of-range reads here are mismatches, matching heap
                // garbage with probability 1 − 2⁻³²).
                var repPos = ip2 - (int)rep1;
                var rval = rep1 > 0 && repPos >= 0
                    ? Read32(source, repPos)
                    : Read32(source, ip2) ^ 1u;

                var current0 = (uint)ip0;
                hashTable[hash0] = current0 + 1; // +1: 0 means empty

                int seqIp, seqMatch, seqLen;
                uint seqOff;
                if (Read32(source, ip2) == rval && rep1 > 0)
                {
                    seqIp = ip2;
                    seqMatch = seqIp - (int)rep1;
                    seqLen = seqIp > 0 && seqMatch > 0 && source[seqIp - 1] == source[seqMatch - 1] ? 1 : 0;
                    seqIp -= seqLen;
                    seqMatch -= seqLen;
                    seqOff = ZstdSeq.Repcode1;
                    seqLen += 4;

                    // Next hash entry already calculated; ip1 precedes ip2.
                    hashTable[hash1] = (uint)ip1 + 1;
                    goto storeMatch;
                }

                if (matchIdx >= 0 && Read32(source, ip0) == Read32(source, matchIdx))
                {
                    // Next hash entry already calculated (ip1 == ip0 + 1).
                    hashTable[hash1] = (uint)ip1 + 1;
                    seqIp = ip0;
                    seqMatch = matchIdx;
                    rep2 = rep1;
                    rep1 = (uint)(seqIp - seqMatch);
                    seqOff = ZstdSeq.OffsetToOffBase(rep1);
                    seqLen = 4;
                    while (seqIp > anchor && seqMatch > 0 && source[seqIp - 1] == source[seqMatch - 1])
                    {
                        seqIp--;
                        seqMatch--;
                        seqLen++;
                    }

                    goto storeMatch;
                }

                // Lookup ip1.
                matchIdx = (int)hashTable[hash1] - 1;

                // Hash ip2.
                hash0 = hash1;
                hash1 = ZstdMatchFinder.HashPtr(source, ip2, hashLog, mls);

                // Advance.
                ip0 = ip1;
                ip1 = ip2;
                ip2 = ip3;

                current0 = (uint)ip0;
                hashTable[hash0] = current0 + 1;

                if (matchIdx >= 0 && Read32(source, ip0) == Read32(source, matchIdx))
                {
                    if (step <= 4)
                    {
                        hashTable[hash1] = (uint)ip1 + 1;
                    }

                    seqIp = ip0;
                    seqMatch = matchIdx;
                    rep2 = rep1;
                    rep1 = (uint)(seqIp - seqMatch);
                    seqOff = ZstdSeq.OffsetToOffBase(rep1);
                    seqLen = 4;
                    while (seqIp > anchor && seqMatch > 0 && source[seqIp - 1] == source[seqMatch - 1])
                    {
                        seqIp--;
                        seqMatch--;
                        seqLen++;
                    }

                    goto storeMatch;
                }

                // Lookup ip1.
                matchIdx = (int)hashTable[hash1] - 1;

                // Hash ip2.
                hash0 = hash1;
                hash1 = ZstdMatchFinder.HashPtr(source, ip2, hashLog, mls);

                // Advance.
                ip0 = ip1;
                ip1 = ip2;
                ip2 = ip0 + step;
                ip3 = ip1 + step;

                if (ip2 >= nextStep)
                {
                    step++;
                    nextStep += StepIncrement;
                }

                if (ip3 >= ilimit)
                {
                    break;
                }

                continue;

            storeMatch:
                // _match: count forward, store, refill, immediate reps.
                seqLen += CountMatches(source, seqIp + seqLen, seqMatch + seqLen, n);
                store.StoreSequence(source.Slice(anchor, seqIp - anchor), seqOff, seqLen);
                ip0 = seqIp + seqLen;
                anchor = ip0;

                if (ip0 <= ilimit)
                {
                    // Guarded: current0+2 may hash past the end (upstream
                    // over-reads; zero-padded here, see class remarks).
                    hashTable[ZstdMatchFinder.HashPtr(source, (int)current0 + 2, hashLog, mls)] = current0 + 2 + 1;
                    hashTable[ZstdMatchFinder.HashPtr(source, ip0 - 2, hashLog, mls)] = (uint)(ip0 - 2) + 1;

                    while (ip0 <= ilimit && rep2 > 0
                        && ip0 - (int)rep2 >= 0 && Read32(source, ip0) == Read32(source, ip0 - (int)rep2))
                    {
                        var repLen = 4 + CountMatches(source, ip0 + 4, ip0 + 4 - (int)rep2, n);
                        (rep2, rep1) = (rep1, rep2);
                        hashTable[ZstdMatchFinder.HashPtr(source, ip0, hashLog, mls)] = (uint)ip0 + 1;
                        store.StoreSequence([], ZstdSeq.Repcode1, repLen);
                        ip0 += repLen;
                        anchor = ip0;
                    }
                }

                matched = true;
                break; // back to _start
            }

            if (!matched)
            {
                break; // inner loop exited via ip3 >= ilimit: _cleanup
            }
        }

        // _cleanup: restore invalidated reps (with the single-invalid rotation).
        saved2 = saved1 != 0 && rep1 != 0 ? saved1 : saved2;
        repeatOffsets[0] = rep1 != 0 ? rep1 : saved1;
        repeatOffsets[1] = rep2 != 0 ? rep2 : saved2;

        store.SetTrailingLiterals(source.Slice(anchor, n - anchor));
        return n - anchor;
    }
}
