using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// FSE compression table: the encoder half of <see cref="ZstdFse.DecodeTable"/>.
/// Layout mirrors <c>FSE_CTable</c> from <c>lib/common/fse.h</c>:
/// next-state values sorted by symbol (<c>tableU16</c>) plus the per-symbol
/// compression transform (<c>deltaNbBits</c> / <c>deltaFindState</c>).
/// Built by <see cref="ZstdFseEncoder.BuildCTable"/>.
/// </summary>
internal sealed class FseCTable
{
    /// <summary>Accuracy log (table size = 1 &lt;&lt; TableLog).</summary>
    public int TableLog;

    /// <summary>Maximum symbol value (inclusive).</summary>
    public int MaxSymbolValue;

    /// <summary>Next-state bases sorted by symbol order (size 1 &lt;&lt; TableLog).</summary>
    public int[] StateTable = [];

    /// <summary>Per-symbol bit-cost contribution (16.16 fixed point).</summary>
    public uint[] DeltaNbBits = [];

    /// <summary>Per-symbol state-index adjustment.</summary>
    public int[] DeltaFindState = [];
}

/// <summary>
/// FSE encoder: histogram normalization, distribution-header writing,
/// compression-table construction, and symbol-stream encoding.
/// C# port of <c>lib/compress/fse_compress.c</c> (plus the inlined
/// <c>FSE_initCState2</c> / <c>FSE_encodeSymbol</c> / <c>FSE_flushCState</c>
/// primitives from <c>lib/common/fse.h</c>).
/// Output is decoded by the existing <see cref="ZstdFse"/> decoder and by
/// native zstd; like the reference, encoding runs back-to-front (LIFO).
/// </summary>
internal static class ZstdFseEncoder
{
    /// <summary>Minimum table log (<c>FSE_MIN_TABLELOG</c>).</summary>
    public const int MinTableLog = 5;

    /// <summary>Maximum table log (<c>FSE_MAX_TABLELOG</c>).</summary>
    public const int MaxTableLog = 12;

    /// <summary>Default table log (<c>FSE_DEFAULT_TABLELOG</c>).</summary>
    public const int DefaultTableLog = 11;

    /// <summary>Worst-case distribution-header size (<c>FSE_NCOUNTBOUND</c>).</summary>
    public const int NCountBound = 512;

    // Rounding table from FSE_normalizeCount (lib/compress/fse_compress.c).
    private static readonly uint[] RtbTable = [0, 473195, 504333, 520860, 550000, 700000, 750000, 830000];

    /// <summary>
    /// Worst-case distribution-header size for the given alphabet
    /// (<c>FSE_NCountWriteBound</c>). Size <see cref="WriteNCount"/> buffers with this.
    /// </summary>
    public static int NCountWriteBound(int maxSymbolValue, int tableLog)
    {
        if (maxSymbolValue == 0)
        {
            return NCountBound;
        }

        return (((maxSymbolValue + 1) * tableLog + 4 + 2) / 8) + 1 + 2;
    }

    /// <summary>
    /// Worst-case size of an <see cref="Encode"/>d symbol stream without its
    /// distribution header (<c>FSE_BLOCKBOUND</c>). Size encode buffers with this.
    /// </summary>
    public static int BlockBound(int srcSize) => srcSize + (srcSize >> 7) + 4 + 8;

    /// <summary>
    /// Worst-case size of header plus stream (<c>FSE_COMPRESSBOUND</c>).
    /// </summary>
    public static int CompressBound(int srcSize) => NCountBound + BlockBound(srcSize);

    /// <summary>
    /// Minimum table log that can represent <paramref name="srcSize"/> symbols
    /// over an alphabet of <paramref name="maxSymbolValue"/> + 1 symbols
    /// (<c>FSE_minTableLog</c>). Normalization below this fails.
    /// </summary>
    public static int MinTableLogFor(int srcSize, int maxSymbolValue)
    {
        if (srcSize <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(srcSize));
        }

        uint minBitsSrc = (uint)BitOperations.Log2((uint)srcSize) + 1;
        uint minBitsSymbols = (uint)BitOperations.Log2((uint)maxSymbolValue) + 2;
        return (int)Math.Min(minBitsSrc, minBitsSymbols);
    }

    /// <summary>
    /// Optimal table log for the distribution (<c>FSE_optimalTableLog</c>:
    /// at most <paramref name="maxTableLog"/> (or <see cref="DefaultTableLog"/>
    /// when 0), reduced for small inputs, raised to <see cref="MinTableLogFor"/>).
    /// </summary>
    public static int OptimalTableLog(int maxTableLog, int srcSize, int maxSymbolValue) =>
        OptimalTableLog(maxTableLog, srcSize, maxSymbolValue, minus: 2);

    /// <summary>
    /// Optimal table log with an explicit accuracy reduction
    /// (<c>FSE_optimalTableLog_internal</c>; FSE uses <c>minus</c> = 2,
    /// Huffman table selection uses 1). Unsigned arithmetic mirrors the
    /// reference, including wrap-around for tiny inputs.
    /// </summary>
    public static int OptimalTableLog(int maxTableLog, int srcSize, int maxSymbolValue, int minus)
    {
        if (srcSize <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(srcSize));
        }

        uint maxBitsSrc = unchecked((uint)(BitOperations.Log2((uint)(srcSize - 1)) - minus));
        uint tableLog = maxTableLog == 0 ? DefaultTableLog : (uint)maxTableLog;
        uint minBits = (uint)MinTableLogFor(srcSize, maxSymbolValue);
        if (maxBitsSrc < tableLog)
        {
            tableLog = maxBitsSrc;
        }

        if (minBits > tableLog)
        {
            tableLog = minBits;
        }

        if (tableLog < MinTableLog)
        {
            tableLog = MinTableLog;
        }

        if (tableLog > MaxTableLog)
        {
            tableLog = MaxTableLog;
        }

        return (int)tableLog;
    }

    /// <summary>
    /// Normalizes a histogram into integer probabilities summing to
    /// <c>1 &lt;&lt; tableLog</c> (<c>FSE_normalizeCount</c>).
    /// Present symbols always get a nonzero probability (1, or -1 for
    /// "less than 1" when <paramref name="useLowProbCount"/> is set).
    /// Returns the table log, or -1 when all counts sit in one symbol
    /// (RLE special case: the caller must emit an RLE block, never a table).
    /// Throws <see cref="ZstdException"/> when the distribution cannot be
    /// represented at this accuracy.
    /// </summary>
    public static int NormalizeCounts(
        short[] normalizedCounter, uint[] count, int total,
        int maxSymbolValue, int tableLog, bool useLowProbCount)
    {
        ArgumentNullException.ThrowIfNull(normalizedCounter);
        ArgumentNullException.ThrowIfNull(count);
        if (normalizedCounter.Length <= maxSymbolValue || count.Length <= maxSymbolValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSymbolValue));
        }

        if (total <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total));
        }

        if (tableLog == 0)
        {
            tableLog = DefaultTableLog;
        }

        if (tableLog < MinTableLog)
        {
            throw new ZstdException("FSE tableLog too small.");
        }

        if (tableLog > MaxTableLog)
        {
            throw new ZstdException("FSE tableLog too large.");
        }

        if (tableLog < MinTableLogFor(total, maxSymbolValue))
        {
            throw new ZstdException("FSE tableLog too small for distribution.");
        }

        short lowProbCount = useLowProbCount ? (short)-1 : (short)1;
        ulong scale = 62UL - (uint)tableLog;
        ulong step = (1UL << 62) / (uint)total;
        ulong vStep = 1UL << (int)(scale - 20);
        int stillToDistribute = 1 << tableLog;
        uint largest = 0;
        short largestP = 0;
        uint lowThreshold = (uint)total >> tableLog;

        for (uint s = 0; s <= (uint)maxSymbolValue; s++)
        {
            if (count[s] == (uint)total)
            {
                return -1; // RLE special case.
            }

            if (count[s] == 0)
            {
                normalizedCounter[s] = 0;
                continue;
            }

            if (count[s] <= lowThreshold)
            {
                normalizedCounter[s] = lowProbCount;
                stillToDistribute--;
            }
            else
            {
                short proba = (short)((count[s] * step) >> (int)scale);
                if (proba < 8)
                {
                    ulong restToBeat = vStep * RtbTable[proba];
                    if ((count[s] * step) - ((ulong)(uint)proba << (int)scale) > restToBeat)
                    {
                        proba++;
                    }
                }

                if (proba > largestP)
                {
                    largestP = proba;
                    largest = s;
                }

                normalizedCounter[s] = proba;
                stillToDistribute -= proba;
            }
        }

        if (-stillToDistribute >= (normalizedCounter[largest] >> 1))
        {
            NormalizeM2(normalizedCounter, tableLog, count, total, maxSymbolValue, lowProbCount);
        }
        else
        {
            normalizedCounter[largest] = (short)(normalizedCounter[largest] + stillToDistribute);
        }

        return tableLog;
    }

    // Secondary normalization for large overshoot (FSE_normalizeM2).
    private static void NormalizeM2(
        short[] norm, int tableLog, uint[] count, int total,
        int maxSymbolValue, short lowProbCount)
    {
        const short NotYetAssigned = -2;
        uint distributed = 0;
        uint lowThreshold = (uint)total >> tableLog;
        uint lowOne = (uint)(((ulong)(uint)total * 3) >> (tableLog + 1));

        for (uint s = 0; s <= (uint)maxSymbolValue; s++)
        {
            if (count[s] == 0)
            {
                norm[s] = 0;
                continue;
            }

            if (count[s] <= lowThreshold)
            {
                norm[s] = lowProbCount;
                distributed++;
                total -= (int)count[s];
                continue;
            }

            if (count[s] <= lowOne)
            {
                norm[s] = 1;
                distributed++;
                total -= (int)count[s];
                continue;
            }

            norm[s] = NotYetAssigned;
        }

        uint toDistribute = unchecked((uint)((1 << tableLog) - (int)distributed));
        if (toDistribute == 0)
        {
            return;
        }

        if ((uint)((ulong)(uint)total / toDistribute) > lowOne)
        {
            lowOne = (uint)((ulong)(uint)total * 3 / (toDistribute * 2));
            for (uint s = 0; s <= (uint)maxSymbolValue; s++)
            {
                if (norm[s] == NotYetAssigned && count[s] <= lowOne)
                {
                    norm[s] = 1;
                    distributed++;
                    total -= (int)count[s];
                }
            }

            toDistribute = unchecked((uint)((1 << tableLog) - (int)distributed));
        }

        if (distributed == (uint)maxSymbolValue + 1)
        {
            // All values are poor; give the remainder to the largest count.
            uint maxV = 0;
            uint maxC = 0;
            for (uint s = 0; s <= (uint)maxSymbolValue; s++)
            {
                if (count[s] > maxC)
                {
                    maxV = s;
                    maxC = count[s];
                }
            }

            norm[maxV] = (short)(norm[maxV] + toDistribute);
            return;
        }

        if (total == 0)
        {
            for (uint s = 0; toDistribute > 0; s = (s + 1) % ((uint)maxSymbolValue + 1))
            {
                if (norm[s] > 0)
                {
                    toDistribute--;
                    norm[s]++;
                }
            }

            return;
        }

        ulong vStepLog = 62UL - (uint)tableLog;
        ulong mid = (1UL << (int)(vStepLog - 1)) - 1;
        ulong rStep = ((1UL << (int)vStepLog) * toDistribute + mid) / (uint)total;
        ulong tmpTotal = mid;
        for (uint s = 0; s <= (uint)maxSymbolValue; s++)
        {
            if (norm[s] == NotYetAssigned)
            {
                ulong end = tmpTotal + (count[s] * rStep);
                uint sStart = (uint)(tmpTotal >> (int)vStepLog);
                uint sEnd = (uint)(end >> (int)vStepLog);
                uint weight = sEnd - sStart;
                if (weight < 1)
                {
                    throw new ZstdException("FSE normalization failed.");
                }

                norm[s] = (short)weight;
                tmpTotal = end;
            }
        }
    }

    /// <summary>
    /// Writes a distribution header (<c>FSE_writeNCount</c>) with a
    /// <see cref="ForwardBitWriter"/>. The existing
    /// <see cref="ZstdFse.ParseNormalizedCounts"/> must decode it back.
    /// Returns bytes written. Throws <see cref="ZstdException"/> on overflow
    /// (size the buffer with <see cref="NCountWriteBound"/>).
    /// </summary>
    public static int WriteNCount(
        byte[] dst, int offset, int capacity,
        short[] norm, int maxSymbolValue, int tableLog)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(norm);
        if (tableLog > MaxTableLog)
        {
            throw new ZstdException("FSE tableLog too large.");
        }

        if (tableLog < MinTableLog)
        {
            throw new ZstdException("FSE tableLog too small.");
        }

        if (norm.Length <= maxSymbolValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSymbolValue));
        }

        var writer = new ForwardBitWriter(dst, offset, capacity);
        writer.AddBits((uint)(tableLog - MinTableLog), 4);

        int tableSize = 1 << tableLog;
        int remaining = tableSize + 1; // +1 for extra accuracy
        int threshold = tableSize;
        int nbBits = tableLog + 1;
        int alphabetSize = maxSymbolValue + 1;
        int symbol = 0;
        bool previousIs0 = false;

        while (symbol < alphabetSize && remaining > 1)
        {
            if (previousIs0)
            {
                int start = symbol;
                while (symbol < alphabetSize && norm[symbol] == 0)
                {
                    symbol++;
                }

                if (symbol == alphabetSize)
                {
                    break; // Incorrect distribution; caught by the remaining check.
                }

                while (symbol >= start + 24)
                {
                    start += 24;
                    writer.AddBits(0xFFFF, 16);
                }

                while (symbol >= start + 3)
                {
                    start += 3;
                    writer.AddBits(3, 2);
                }

                writer.AddBits((uint)(symbol - start), 2);
            }

            int count = norm[symbol++];
            int max = (2 * threshold - 1) - remaining;
            remaining -= count < 0 ? -count : count;
            count++; // +1 for extra accuracy
            if (count >= threshold)
            {
                count += max;
            }

            writer.AddBits((uint)count, count < max ? nbBits - 1 : nbBits);
            previousIs0 = count == 1;
            if (remaining < 1)
            {
                throw new ZstdException("Bad FSE distribution.");
            }

            while (remaining < threshold)
            {
                nbBits--;
                threshold >>= 1;
            }
        }

        if (remaining != 1)
        {
            throw new ZstdException("Bad FSE distribution.");
        }

        return writer.Flush();
    }

    /// <summary>
    /// Builds a compression table (<c>FSE_buildCTable</c>). The state spread
    /// matches the inverse of <see cref="ZstdFse.BuildTable"/>; verify by
    /// compress→decompress of symbol streams, not by comparing to C.
    /// </summary>
    public static FseCTable BuildCTable(short[] norm, int maxSymbolValue, int tableLog)
    {
        ArgumentNullException.ThrowIfNull(norm);
        if (tableLog < 1 || tableLog > 15)
        {
            throw new ZstdException("Invalid FSE tableLog.");
        }

        if (norm.Length <= maxSymbolValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSymbolValue));
        }

        int tableSize = 1 << tableLog;
        int tableMask = tableSize - 1;
        int maxSV1 = maxSymbolValue + 1;
        int step = (tableSize >> 1) + (tableSize >> 3) + 3;

        // Sanity: probabilities must sum to exactly tableSize (-1 counts 1).
        long total = 0;
        for (int s = 0; s <= maxSymbolValue; s++)
        {
            if (norm[s] < -1)
            {
                throw new ZstdException("Bad FSE distribution.");
            }

            total += norm[s] == -1 ? 1 : norm[s];
        }

        if (total != tableSize)
        {
            throw new ZstdException("Bad FSE distribution.");
        }

        int[] tableSymbol = new int[tableSize];
        int[] cumul = new int[maxSV1 + 1];
        int highThreshold = tableSize - 1;

        // Symbol start positions; low-probability symbols take cells from the top.
        for (int u = 1; u <= maxSV1; u++)
        {
            if (norm[u - 1] == -1)
            {
                cumul[u] = cumul[u - 1] + 1;
                tableSymbol[highThreshold--] = u - 1;
            }
            else
            {
                cumul[u] = cumul[u - 1] + norm[u - 1];
            }
        }

        cumul[maxSV1] = tableSize + 1;

        // Spread symbols (general path; identical result to the unrolled
        // no-lowprob fast path in C, which is only a speed optimization).
        int position = 0;
        for (int symbol = 0; symbol < maxSV1; symbol++)
        {
            int freq = norm[symbol];
            for (int i = 0; i < freq; i++)
            {
                tableSymbol[position] = symbol;
                position = (position + step) & tableMask;
                while (position > highThreshold)
                {
                    position = (position + step) & tableMask; // Low proba area.
                }
            }
        }

        if (position != 0)
        {
            throw new ZstdException("Bad FSE distribution.");
        }

        // TableU16: next-state values sorted by symbol order.
        int[] stateTable = new int[tableSize];
        for (int u = 0; u < tableSize; u++)
        {
            int s = tableSymbol[u];
            stateTable[cumul[s]++] = tableSize + u;
        }

        // Symbol transformation table.
        uint[] deltaNbBits = new uint[maxSV1];
        int[] deltaFindState = new int[maxSV1];
        uint distributed = 0;
        for (int s = 0; s <= maxSymbolValue; s++)
        {
            switch (norm[s])
            {
                case 0:
                    // Compatibility fill (FSE_getMaxNbBits).
                    deltaNbBits[s] = (uint)(((tableLog + 1) << 16) - (1 << tableLog));
                    break;
                case -1:
                case 1:
                    deltaNbBits[s] = (uint)((tableLog << 16) - (1 << tableLog));
                    deltaFindState[s] = (int)(distributed - 1);
                    distributed++;
                    break;
                default:
                {
                    uint maxBitsOut = (uint)(tableLog - BitOperations.Log2((uint)norm[s] - 1));
                    uint minStatePlus = (uint)norm[s] << (int)maxBitsOut;
                    deltaNbBits[s] = (maxBitsOut << 16) - minStatePlus;
                    deltaFindState[s] = (int)(distributed - (uint)norm[s]);
                    distributed += (uint)norm[s];
                    break;
                }
            }
        }

        return new FseCTable
        {
            TableLog = tableLog,
            MaxSymbolValue = maxSymbolValue,
            StateTable = stateTable,
            DeltaNbBits = deltaNbBits,
            DeltaFindState = deltaFindState,
        };
    }

    /// <summary>
    /// True when every symbol in range has the same value (RLE shape:
    /// the caller must emit an RLE block instead of calling <see cref="Encode"/>).
    /// </summary>
    public static bool IsSingleSymbol(byte[] src, int offset, int length, out byte symbol)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        symbol = src[offset];
        for (int i = 1; i < length; i++)
        {
            if (src[offset + i] != symbol)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Encodes a symbol stream with two interleaved states
    /// (<c>FSE_compress_usingCTable</c>). Symbols are consumed back-to-front;
    /// the decoder reads them front-to-back (LIFO).
    /// Returns bytes written, or -1 when FSE encoding does not apply:
    /// <paramref name="srcLength"/> ≤ 2, RLE-shaped input, an RLE table, or a
    /// destination too small (size it with <see cref="BlockBound"/>).
    /// A container-overflow <see cref="ZstdException"/> still throws: with a
    /// valid table that is always a caller bug, never a size decision.
    /// </summary>
    public static int Encode(
        byte[] dst, int dstOffset, int dstCapacity,
        byte[] src, int srcOffset, int srcLength, FseCTable ct)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(ct);
        if (srcLength <= 2)
        {
            return -1;
        }

        if (ct.TableLog == 0)
        {
            return -1;
        }

        if (IsSingleSymbol(src, srcOffset, srcLength, out _))
        {
            return -1;
        }

        CStreamWriter bitC;
        try
        {
            bitC = new CStreamWriter(dst, dstOffset, dstCapacity);
        }
        catch (ZstdException)
        {
            return -1; // Mirrors BIT_initCStream failure → compress returns 0.
        }

        int tableLog = ct.TableLog;
        long state1 = 1L << tableLog;
        long state2 = 1L << tableLog;

        int ip = srcOffset + srcLength; // One past the end; moves backwards.
        int istart = srcOffset;

        if ((srcLength & 1) == 1)
        {
            state1 = InitState2(ct, src[--ip]);
            state2 = InitState2(ct, src[--ip]);
            state1 = EncodeSymbol(bitC, ct, state1, src[--ip]);
            bitC.FlushBits();
        }
        else
        {
            state2 = InitState2(ct, src[--ip]);
            state1 = InitState2(ct, src[--ip]);
        }

        // Join to mod 4 (64-bit container: 4 symbols per steady-state loop).
        int srcSize = srcLength - 2;
        if ((srcSize & 2) != 0)
        {
            state2 = EncodeSymbol(bitC, ct, state2, src[--ip]);
            state1 = EncodeSymbol(bitC, ct, state1, src[--ip]);
            bitC.FlushBits();
        }

        while (ip > istart)
        {
            state2 = EncodeSymbol(bitC, ct, state2, src[--ip]);
            state1 = EncodeSymbol(bitC, ct, state1, src[--ip]);
            state2 = EncodeSymbol(bitC, ct, state2, src[--ip]);
            state1 = EncodeSymbol(bitC, ct, state1, src[--ip]);
            bitC.FlushBits();
        }

        bitC.AddBits((ulong)state2, tableLog); // FSE_flushCState(CState2).
        bitC.FlushBits();
        bitC.AddBits((ulong)state1, tableLog); // FSE_flushCState(CState1).
        bitC.FlushBits();

        try
        {
            return bitC.Close(); // 0 in C on overflow → -1 here.
        }
        catch (ZstdException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Degenerate RLE compression table: every state decodes to
    /// <paramref name="symbol"/> and no bits are ever emitted
    /// (mirrors <c>FSE_buildCTable_rle</c>: tableLog 0, so the decoder's
    /// <c>readBits(0)</c> consumes nothing). Used by the sequence encoder for
    /// single-symbol alphabets, which still occupy a state slot in the shared
    /// sequence bitstream.
    /// </summary>
    internal static FseCTable RleTable(int symbol) => new()
    {
        TableLog = 0,
        MaxSymbolValue = symbol,
        StateTable = [0],
        DeltaNbBits = new uint[symbol + 1],
        DeltaFindState = new int[symbol + 1],
    };

    /// <summary>
    /// Single-state initializer (<c>FSE_initCState2</c>) for encoders that share
    /// one <see cref="CStreamWriter"/> across alphabets (sequence bitstream).
    /// No bits are emitted here; the state is written later by
    /// <see cref="FlushCState"/>. RLE tables yield state 0 and emit nothing.
    /// </summary>
    internal static long InitCState2(FseCTable ct, byte symbol)
    {
        ArgumentNullException.ThrowIfNull(ct);
        if (ct.TableLog == 0)
        {
            return 0;
        }

        return InitState2(ct, symbol);
    }

    /// <summary>
    /// Single-state symbol encoding (<c>FSE_encodeSymbol</c>) into a shared
    /// stream. Emits the state's low bits (caller owns flush scheduling, as in
    /// <c>ZSTD_encodeSequences_body</c>) and returns the next state. RLE tables
    /// are a no-op returning the state unchanged.
    /// </summary>
    internal static long EncodeCStateSymbol(CStreamWriter bitC, FseCTable ct, long state, byte symbol)
    {
        ArgumentNullException.ThrowIfNull(bitC);
        ArgumentNullException.ThrowIfNull(ct);
        if (ct.TableLog == 0)
        {
            return state;
        }

        return EncodeSymbol(bitC, ct, state, symbol);
    }

    /// <summary>
    /// Writes a final state with <paramref name="tableLog"/> bits
    /// (<c>FSE_flushCState</c>). RLE tables (log 0) emit nothing.
    /// </summary>
    internal static void FlushCState(CStreamWriter bitC, long state, int tableLog)
    {
        ArgumentNullException.ThrowIfNull(bitC);
        if (tableLog > 0)
        {
            bitC.AddBits((ulong)state, tableLog);
        }
    }

    // FSE_initCState2: init, then fold in the first (== last decoded) symbol
    // using the smallest state value possible.
    private static long InitState2(FseCTable ct, byte symbol)
    {
        if (symbol > ct.MaxSymbolValue)
        {
            throw new ZstdException("FSE symbol out of range.");
        }

        uint deltaNbBits = ct.DeltaNbBits[symbol];
        int nbBitsOut = (int)((deltaNbBits + (1 << 15)) >> 16);
        long value = ((long)nbBitsOut << 16) - deltaNbBits;
        return ct.StateTable[(int)((value >> nbBitsOut) + ct.DeltaFindState[symbol])];
    }

    // FSE_encodeSymbol: emit low bits of the state, look up the next state.
    private static long EncodeSymbol(CStreamWriter bitC, FseCTable ct, long state, byte symbol)
    {
        if (symbol > ct.MaxSymbolValue)
        {
            throw new ZstdException("FSE symbol out of range.");
        }

        uint deltaNbBits = ct.DeltaNbBits[symbol];
        int nbBitsOut = (int)((state + deltaNbBits) >> 16);
        bitC.AddBits((ulong)state, nbBitsOut);
        return ct.StateTable[(int)((state >> nbBitsOut) + ct.DeltaFindState[symbol])];
    }
}
