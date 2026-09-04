using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// Exact C# port of the binary-tree (DUBT) search used by
/// <c>ZSTD_compressBlock_btlazy2</c> — <c>ZSTD_updateDUBT</c>,
/// <c>ZSTD_insertDUBT1</c>, <c>ZSTD_DUBT_findBestMatch</c> and
/// <c>ZSTD_BtFindBestMatch</c> (<c>lib/compress/zstd_lazy.c</c>, zstd-1.5.7),
/// <c>ZSTD_noDict</c> subset (dictionary branches deleted).
/// The tree lives in the chain-table memory: two slots per
/// <c>btMask</c> entry (<c>btLog = chainLog - 1</c>), smaller / larger links.
/// <para/>
/// Index discipline: table slots store <c>pos + 1</c> (never bare <c>pos</c>),
/// mirroring upstream where absolute indices start at
/// <c>ZSTD_WINDOW_START_INDEX (2)</c> and slot value 0 means "empty". The
/// unsorted mark uses a dedicated sentinel (<see cref="UnsortedMark"/>) that
/// can never collide with a stored position; it converts to −1 (below every
/// <c>lowLimit</c>) wherever a slot is read as a position, exactly like
/// upstream where mark value 1 sits below the lowest valid index 2.
/// Deliberate guards: <c>ZSTD_hashPtr</c> reads are zero-padded at the tail
/// (upstream over-reads there); every candidate is verified in bounds.
/// The end-of-block compare hazard needs no guard: the first candidate whose
/// match reaches <c>iend</c> strictly improves (earlier candidates cannot
/// reach it, see remarks) and breaks before any out-of-range compare.
/// </summary>
internal static class ZstdBinaryTree
{
    /// <summary>
    /// Unsorted-slot mark (<c>ZSTD_DUBT_UNSORTED_MARK</c>). Stored only in odd
    /// (larger-link) tree slots; reads as a position convert it to −1.
    /// </summary>
    internal const uint UnsortedMark = 0xFFFFFFFFu;

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

    /// <summary>Converts a raw tree/hash slot to a position (−1 = empty/mark).</summary>
    private static int SlotPos(uint raw)
    {
        return raw == UnsortedMark ? -1 : (int)raw - 1;
    }

    /// <summary>
    /// <c>ZSTD_BtFindBestMatch</c>, noDict: skips the already-updated area,
    /// inserts positions up to <paramref name="ip"/>, and returns the best
    /// match length at <paramref name="ip"/> (≥ 3, i.e. possibly below
    /// <c>mls</c>), setting <paramref name="offBase"/> on improvement.
    /// </summary>
    internal static int BtFindBestMatch(
        ReadOnlySpan<byte> src, int end, int ip, ref uint offBase,
        int mls, int hashLog, int searchLog, int chainLog, int windowLog,
        uint[] hashTable, uint[] bt,
        ref int nextToUpdate)
    {
        if (ip < nextToUpdate)
        {
            return 0; // skipped area (matchEndIdx jump)
        }

        UpdateDubt(src, ip, mls, hashLog, chainLog, hashTable, bt, ref nextToUpdate);
        return DubtFindBestMatch(
            src, end, ip, ref offBase, mls, hashLog, searchLog, chainLog, windowLog,
            hashTable, bt, ref nextToUpdate);
    }

    /// <summary>
    /// <c>ZSTD_updateDUBT</c>: inserts <c>[nextToUpdate, target)</c> into the
    /// hash table and links each into the tree like a chain, marked unsorted.
    /// </summary>
    private static void UpdateDubt(
        ReadOnlySpan<byte> src, int target, int mls, int hashLog, int chainLog,
        uint[] hashTable, uint[] bt, ref int nextToUpdate)
    {
        var btMask = (1 << (chainLog - 1)) - 1;
        var idx = nextToUpdate;
        for (; idx < target; idx++)
        {
            var h = ZstdMatchFinder.HashPtr(src, idx, hashLog, mls);
            var matchIndex = hashTable[h];
            var slot = 2 * (idx & btMask);
            hashTable[h] = (uint)idx + 1; // +1: 0 means empty
            bt[slot] = matchIndex; // update BT like a chain
            bt[slot + 1] = UnsortedMark;
        }

        nextToUpdate = target;
    }

    /// <summary>
    /// <c>ZSTD_insertDUBT1</c>, noDict: sorts one already-inserted but
    /// unsorted position into the tree, comparing at most
    /// <paramref name="nbCompares"/> candidates above
    /// <paramref name="btLow"/>/windowLow.
    /// </summary>
    private static void InsertDubt1(
        ReadOnlySpan<byte> src, int end, int curr, uint nbCompares, int btLow,
        int windowLog, uint[] bt, int btMask)
    {
        var maxDistance = 1 << windowLog;
        var windowLow = curr > maxDistance ? curr - maxDistance : 0;
        var commonSmaller = 0;
        var commonLarger = 0;
        int? smallerSlot = 2 * (curr & btMask);
        int? largerSlot = smallerSlot + 1;
        var matchIndex = SlotPos(bt[smallerSlot.Value]);

        for (; nbCompares > 0 && matchIndex > windowLow; nbCompares--)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            var matchLength = Math.Min(commonSmaller, commonLarger);
            matchLength += CountMatches(src, curr + matchLength, matchIndex + matchLength, end);

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
    }

    /// <summary>
    /// <c>ZSTD_DUBT_findBestMatch</c>, noDict: sorts stacked unsorted
    /// candidates, then walks the tree from the hash-table head for at most
    /// <c>1 &lt;&lt; searchLog</c> compares, keeping the longest match with
    /// the gain tiebreak. Always advances <paramref name="nextToUpdate"/>
    /// past <paramref name="ip"/> (repetitive-pattern skip).
    /// </summary>
    private static int DubtFindBestMatch(
        ReadOnlySpan<byte> src, int end, int ip, ref uint offBase,
        int mls, int hashLog, int searchLog, int chainLog, int windowLog,
        uint[] hashTable, uint[] bt,
        ref int nextToUpdate)
    {
        var btLog = chainLog - 1;
        var btMask = (1 << btLog) - 1;
        var btLow = btMask >= ip ? 0 : ip - btMask;
        var windowLow = WindowLow(ip, windowLog);
        var unsortLimit = Math.Max(btLow, windowLow);

        var h = ZstdMatchFinder.HashPtr(src, ip, hashLog, mls);
        var matchIndex = SlotPos(hashTable[h]);

        var nbCompares = (uint)(1 << searchLog);
        var nbCandidates = nbCompares;
        var previousCandidate = -1;

        // Reach end of unsorted candidates list.
        while (matchIndex > unsortLimit
            && bt[2 * (matchIndex & btMask) + 1] == UnsortedMark
            && nbCandidates > 1)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            bt[nextSlot + 1] = previousCandidate >= 0 ? (uint)previousCandidate + 1 : 0;
            previousCandidate = matchIndex;
            matchIndex = SlotPos(bt[nextSlot]);
            nbCandidates--;
        }

        // Nullify last candidate if still unsorted.
        if (matchIndex > unsortLimit
            && bt[2 * (matchIndex & btMask) + 1] == UnsortedMark)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            bt[nextSlot] = bt[nextSlot + 1] = 0;
        }

        // Batch sort stacked candidates.
        matchIndex = previousCandidate;
        while (matchIndex >= 0)
        {
            var nextSlot = 2 * (matchIndex & btMask) + 1;
            var nextCandidateIdx = SlotPos(bt[nextSlot]);
            InsertDubt1(src, end, matchIndex, nbCandidates, unsortLimit, windowLog, bt, btMask);
            matchIndex = nextCandidateIdx;
            nbCandidates++;
        }

        // Find longest match.
        var commonSmaller = 0;
        var commonLarger = 0;
        int? smallerSlot = 2 * (ip & btMask);
        int? largerSlot = smallerSlot + 1;
        var matchEndIdx = ip + 8 + 1;
        var bestLength = 0;

        matchIndex = SlotPos(hashTable[h]);
        // hashTable[h] update (native line 322).
        // NOTE: applied after reading above; written here to mirror order.
        hashTable[h] = (uint)ip + 1;

        for (; nbCompares > 0 && matchIndex > windowLow; nbCompares--)
        {
            var nextSlot = 2 * (matchIndex & btMask);
            var matchLength = Math.Min(commonSmaller, commonLarger);
            matchLength += CountMatches(src, ip + matchLength, matchIndex + matchLength, end);

            if (matchLength > bestLength)
            {
                if (matchLength > matchEndIdx - matchIndex)
                {
                    matchEndIdx = matchIndex + matchLength;
                }

                if (4 * (matchLength - bestLength)
                    > Highbit32((uint)(ip - matchIndex + 1)) - Highbit32(offBase))
                {
                    bestLength = matchLength;
                    offBase = ZstdSeq.OffsetToOffBase((uint)(ip - matchIndex));
                }

                if (ip + matchLength == end)
                {
                    break; // drop, to guarantee consistency
                }
            }

            if (src[matchIndex + matchLength] < src[ip + matchLength])
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
        return bestLength;
    }
}
