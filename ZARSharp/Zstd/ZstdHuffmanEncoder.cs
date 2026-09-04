using System.Numerics;
using System.Runtime.InteropServices;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace ZARSharp.Zstd;

/// <summary>
/// Huffman compression table: the encoder half of
/// <see cref="ZstdHuffman.HuffmanTable"/>. Stores the code length and the
/// canonical code value per symbol (<c>HUF_CElt</c> from
/// <c>lib/common/huf.h</c>: low 8 bits = length, value in the top bits).
/// Built by <see cref="ZstdHuffmanEncoder.BuildCTable"/>.
/// </summary>
internal sealed class HuffmanCTable
{
    /// <summary>Maximum code length in bits (≤ 11 per RFC 8878).</summary>
    public int TableLog;

    /// <summary>Maximum symbol value (inclusive).</summary>
    public int MaxSymbolValue;

    /// <summary>Code length per symbol (0 = absent).</summary>
    public byte[] NbBits = [];

    /// <summary>Canonical code value per symbol (top-aligned when emitted).</summary>
    public uint[] Codes = [];
}

/// <summary>
/// Huffman encoder: tree building, table-description writing, and 1X/4X
/// symbol-stream encoding. C# port of <c>lib/compress/huf_compress.c</c>
/// (tree: <c>HUF_sort</c> / <c>HUF_buildTree</c> / <c>HUF_setMaxHeight</c>;
/// header: <c>HUF_writeCTable_wksp</c> / <c>HUF_compressWeights</c>;
/// streams: <c>HUF_compress1X/4X_usingCTable</c> over a <c>HUF_CStream_t</c>).
/// Output is decoded by the existing <see cref="ZstdHuffman"/> decoder and by
/// native zstd; like the reference, symbols are encoded back-to-front (LIFO)
/// into a top-anchored bit container, terminated by a 1-bit end mark that the
/// decoder finds as the highest set bit of the last byte (RFC 8878 4.2.2).
/// </summary>
internal static class ZstdHuffmanEncoder
{
    /// <summary>Maximum runtime table log (<c>HUF_TABLELOG_MAX</c>).</summary>
    public const int TableLogMax = 12;

    /// <summary>Default table log, also the literals ceiling
    /// (<c>HUF_TABLELOG_DEFAULT</c> == <c>LitHufLog</c> == 11).</summary>
    public const int TableLogDefault = 11;

    /// <summary>Maximum symbol value (<c>HUF_SYMBOLVALUE_MAX</c>).</summary>
    public const int SymbolValueMax = 255;

    /// <summary>Table-description bound (<c>HUF_CTABLEBOUND</c>).</summary>
    public const int CTableBound = 129;

    /// <summary>Accuracy log used for the FSE-compressed weights header.</summary>
    public const int MaxFseTableLogForHuffHeader = 6;

    /// <summary>
    /// Below this input size a single stream is used
    /// (<c>ZSTD_compressLiterals</c>: <c>singleStream = srcSize &lt; 256</c>);
    /// at or above it four streams with a 6-byte jump table.
    /// </summary>
    public const int SingleStreamThreshold = 256;

    /// <summary>Minimum input for four streams (<c>HUF_compress4X</c>).</summary>
    public const int FourStreamsMinInput = 12;

    /// <summary>Current block size limit (<c>HUF_BLOCKSIZE_MAX</c>).</summary>
    public const int BlockSizeMax = 128 * 1024;

    /// <summary>
    /// Worst-case stream size without the table description
    /// (<c>HUF_BLOCKBOUND</c>).
    /// </summary>
    public static int BlockBound(int srcSize)
    {
        return srcSize + (srcSize >> 8) + 8;
    }

    /// <summary>
    /// Worst-case size of table description plus streams
    /// (<c>HUF_COMPRESSBOUND</c>). Size encode buffers with this.
    /// </summary>
    public static int CompressBound(int srcSize)
    {
        return CTableBound + BlockBound(srcSize);
    }

    /// <summary>
    /// Number of distinct symbols with nonzero count (<c>HUF_cardinality</c>).
    /// </summary>
    public static int Cardinality(uint[] count, int maxSymbolValue)
    {
        var cardinality = 0;
        for (var i = 0; i <= maxSymbolValue; i++)
        {
            if (count[i] != 0)
            {
                cardinality++;
            }
        }

        return cardinality;
    }

    /// <summary>
    /// Minimum table log holding <paramref name="symbolCardinality"/> symbols
    /// (<c>HUF_minTableLog</c>).
    /// </summary>
    public static int MinTableLog(int symbolCardinality)
    {
        return BitOperations.Log2((uint)symbolCardinality) + 1;
    }

    /// <summary>
    /// Cheap table-log selection (<c>HUF_optimalTableLog</c> without the
    /// optimal-depth probing, which upstream only enables for strategies we do
    /// not implement): <c>FSE_optimalTableLog_internal</c> with minus = 1,
    /// capped at 11 bits so the existing <see cref="ZstdHuffman"/> decoder
    /// (RFC 8878: codes ≤ 11 bits) accepts every table.
    /// </summary>
    public static int OptimalTableLog(int srcSize, int maxSymbolValue, int maxTableLog = TableLogDefault)
    {
        var tableLog = ZstdFseEncoder.OptimalTableLog(maxTableLog, srcSize, maxSymbolValue, minus: 1);
        return Math.Min(tableLog, TableLogDefault);
    }

    /// <summary>
    /// Estimated stream size in bytes, without the table description
    /// (<c>HUF_estimateCompressedSize</c>).
    /// </summary>
    public static long EstimateCompressedSize(HuffmanCTable table, uint[] count, int maxSymbolValue)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(count);

        long nbBits = 0;
        for (var s = 0; s <= maxSymbolValue; s++)
        {
            nbBits += (long)table.NbBits[s] * count[s];
        }

        return nbBits >> 3;
    }

    /// <summary>
    /// Builds a Huffman compression table from a histogram
    /// (<c>HUF_buildCTable_wksp</c>: sort → unlimited tree → enforce
    /// <paramref name="tableLog"/> → canonical codes). Returns the table and
    /// the actual maximum code length (which can be smaller than requested).
    /// Throws <see cref="ZstdException"/> when no valid table exists
    /// (fewer than 2 distinct symbols, or depth above the absolute maximum).
    /// </summary>
    public static (HuffmanCTable Table, int MaxBits) BuildCTable(uint[] count, int maxSymbolValue, int tableLog)
    {
        ArgumentNullException.ThrowIfNull(count);
        if (maxSymbolValue < 0 || maxSymbolValue > SymbolValueMax || count.Length <= maxSymbolValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSymbolValue));
        }

        if (tableLog == 0)
        {
            tableLog = TableLogDefault;
        }

        // Sort symbols by decreasing count (HUF_sort); ties by ascending
        // symbol for determinism (the reference leaves ties unspecified).
        var alphabetSize = maxSymbolValue + 1;
        var order = new int[alphabetSize];
        for (var i = 0; i < alphabetSize; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var c = count[b].CompareTo(count[a]);
            return c != 0 ? c : a.CompareTo(b);
        });

        // huffNode array with 1-based indexing (huffNode == huffNode0 + 1):
        // leaves at t[1 + i], internal nodes at t[1 + n], barrier at t[0].
        var t = new NodeElt[(2 * (SymbolValueMax + 1)) + 1];
        for (var i = 0; i < alphabetSize; i++)
        {
            t[1 + i].Count = count[order[i]];
            t[1 + i].Symbol = (byte)order[i];
        }

        var nonNullRank = maxSymbolValue;
        while (nonNullRank > 0 && t[1 + nonNullRank].Count == 0)
        {
            nonNullRank--;
        }

        if (nonNullRank < 1)
        {
            throw new ZstdException("Huffman table needs at least 2 symbols.");
        }

        BuildTree(t, nonNullRank);

        var maxNbBits = SetMaxHeight(t, nonNullRank, tableLog);
        if (maxNbBits > TableLogMax)
        {
            throw new ZstdException("Huffman tree too deep.");
        }

        var table = new HuffmanCTable
        {
            TableLog = maxNbBits,
            MaxSymbolValue = maxSymbolValue,
            NbBits = new byte[SymbolValueMax + 1],
            Codes = new uint[SymbolValueMax + 1],
        };
        BuildTableFromTree(table, t, nonNullRank, maxNbBits);
        return (table, maxNbBits);
    }

    // HUF_buildTree: merges the two smallest nodes until one root remains,
    // then distributes depths from the root down.
    private static void BuildTree(NodeElt[] t, int nonNullRank)
    {
        const int startNode = SymbolValueMax + 1; // STARTNODE
        var lowS = nonNullRank;
        var lowN = startNode;
        var nodeNb = startNode;
        var nodeRoot = nodeNb + lowS - 1;

        t[1 + nodeNb].Count = t[1 + lowS].Count + t[1 + lowS - 1].Count;
        t[1 + lowS].Parent = t[1 + lowS - 1].Parent = (ushort)nodeNb;
        nodeNb++;
        lowS -= 2;
        for (var n = nodeNb; n <= nodeRoot; n++)
        {
            t[1 + n].Count = 1u << 30;
        }

        t[0].Count = 1u << 31; // Barrier: huffNode0[0], read as huffNode[-1].

        while (nodeNb <= nodeRoot)
        {
            var countS = lowS >= 0 ? t[1 + lowS].Count : 0x80000000u;
            var n1 = countS < t[1 + lowN].Count ? lowS-- : lowN++;
            countS = lowS >= 0 ? t[1 + lowS].Count : 0x80000000u;
            var n2 = countS < t[1 + lowN].Count ? lowS-- : lowN++;
            t[1 + nodeNb].Count = t[1 + n1].Count + t[1 + n2].Count;
            t[1 + n1].Parent = t[1 + n2].Parent = (ushort)nodeNb;
            nodeNb++;
        }

        t[1 + nodeRoot].NbBits = 0;
        for (var n = nodeRoot - 1; n >= startNode; n--)
        {
            t[1 + n].NbBits = (byte)(t[1 + t[1 + n].Parent].NbBits + 1);
        }

        for (var n = 0; n <= nonNullRank; n++)
        {
            t[1 + n].NbBits = (byte)(t[1 + t[1 + n].Parent].NbBits + 1);
        }
    }

    // HUF_setMaxHeight: caps every depth at targetNbBits, then pays back the
    // freed rank budget to the cheapest symbols so the tree stays canonical.
    private static int SetMaxHeight(NodeElt[] t, int lastNonNull, int targetNbBits)
    {
        int largestBits = t[1 + lastNonNull].NbBits;
        if (largestBits <= targetNbBits)
        {
            return largestBits;
        }

        var totalCost = 0;
        var baseCost = 1 << (largestBits - targetNbBits);
        var n = lastNonNull;

        while (t[1 + n].NbBits > targetNbBits)
        {
            totalCost += baseCost - (1 << (largestBits - t[1 + n].NbBits));
            t[1 + n].NbBits = (byte)targetNbBits;
            n--;
        }

        while (t[1 + n].NbBits == targetNbBits)
        {
            n--;
        }

        totalCost >>= largestBits - targetNbBits;

        const uint noSymbol = 0xF0F0F0F0;
        var rankLast = new uint[TableLogMax + 2];
        for (var i = 0; i < rankLast.Length; i++)
        {
            rankLast[i] = noSymbol;
        }

        var currentNbBits = targetNbBits;
        for (var pos = n; pos >= 0; pos--)
        {
            if (t[1 + pos].NbBits >= currentNbBits)
            {
                continue;
            }

            currentNbBits = t[1 + pos].NbBits;
            rankLast[targetNbBits - currentNbBits] = (uint)pos;
        }

        while (totalCost > 0)
        {
            var nBitsToDecrease = BitOperations.Log2((uint)totalCost) + 1;
            for (; nBitsToDecrease > 1; nBitsToDecrease--)
            {
                var highPos = rankLast[nBitsToDecrease];
                var lowPos = rankLast[nBitsToDecrease - 1];
                if (highPos == noSymbol)
                {
                    continue;
                }

                if (lowPos == noSymbol)
                {
                    break;
                }

                var highTotal = t[1 + (int)highPos].Count;
                var lowTotal = 2 * t[1 + (int)lowPos].Count;
                if (highTotal <= lowTotal)
                {
                    break;
                }
            }

            while (nBitsToDecrease <= TableLogMax && rankLast[nBitsToDecrease] == noSymbol)
            {
                nBitsToDecrease++;
            }

            totalCost -= 1 << (nBitsToDecrease - 1);
            t[1 + (int)rankLast[nBitsToDecrease]].NbBits++;

            if (rankLast[nBitsToDecrease - 1] == noSymbol)
            {
                rankLast[nBitsToDecrease - 1] = rankLast[nBitsToDecrease];
            }

            if (rankLast[nBitsToDecrease] == 0)
            {
                rankLast[nBitsToDecrease] = noSymbol;
            }
            else
            {
                rankLast[nBitsToDecrease]--;
                if (t[1 + (int)rankLast[nBitsToDecrease]].NbBits != targetNbBits - nBitsToDecrease)
                {
                    rankLast[nBitsToDecrease] = noSymbol;
                }
            }
        }

        while (totalCost < 0)
        {
            if (rankLast[1] == noSymbol)
            {
                while (t[1 + n].NbBits == targetNbBits)
                {
                    n--;
                }

                t[1 + n + 1].NbBits--;
                rankLast[1] = (uint)(n + 1);
                totalCost++;
                continue;
            }

            t[1 + (int)rankLast[1] + 1].NbBits--;
            rankLast[1]++;
            totalCost++;
        }

        return targetNbBits;
    }

    // HUF_buildCTableFromTree: canonical code assignment (per-rank start
    // values, then symbol-order values within each rank).
    private static void BuildTableFromTree(HuffmanCTable table, NodeElt[] t, int nonNullRank, int maxNbBits)
    {
        var nbPerRank = new int[TableLogMax + 1];
        var valPerRank = new int[TableLogMax + 1];
        for (var i = 0; i <= nonNullRank; i++)
        {
            nbPerRank[t[1 + i].NbBits]++;
        }

        var min = 0;
        for (var i = maxNbBits; i > 0; i--)
        {
            valPerRank[i] = min;
            min += nbPerRank[i];
            min >>= 1;
        }

        for (var i = 0; i <= nonNullRank; i++)
        {
            table.NbBits[t[1 + i].Symbol] = t[1 + i].NbBits;
        }

        var alphabetSize = table.MaxSymbolValue + 1;
        for (var i = 0; i < alphabetSize; i++)
        {
            int nbBits = table.NbBits[i];
            if (nbBits > 0)
            {
                table.Codes[i] = (uint)valPerRank[nbBits]++;
            }
        }
    }

    /// <summary>
    /// Writes a Huffman table description (<c>HUF_writeCTable_wksp</c>): code
    /// lengths become weights (<c>huffLog + 1 - nbBits</c>), transmitted for
    /// symbols <c>0 .. maxSymbolValue - 1</c> with the last weight implied by
    /// the decoder. Tries the FSE-compressed form first
    /// (<c>HUF_compressWeights</c>, chosen when it takes less than half the
    /// direct form), else the direct nibble-packed form. Returns bytes
    /// written, or 0 when no description fits (caller must store raw).
    /// </summary>
    public static int WriteCTable(byte[] dst, int offset, int capacity, HuffmanCTable table)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(table);

        var maxSymbolValue = table.MaxSymbolValue;
        var huffLog = table.TableLog;
        if (maxSymbolValue < 1 || maxSymbolValue > SymbolValueMax)
        {
            return 0;
        }

        var weights = new byte[maxSymbolValue];
        for (var i = 0; i < maxSymbolValue; i++)
        {
            int nbBits = table.NbBits[i];
            weights[i] = nbBits == 0 ? (byte)0 : (byte)(huffLog + 1 - nbBits);
        }

        if (capacity < 1)
        {
            return 0;
        }

        var hSize = CompressWeights(dst, offset + 1, capacity - 1, weights, maxSymbolValue);
        if (hSize > 1 && hSize < maxSymbolValue / 2)
        {
            dst[offset] = (byte)hSize;
            return hSize + 1;
        }

        // Direct nibble-packed form. Transmitting more than 128 weights would
        // exceed the header byte the decoder accepts; fall back to raw then
        // (the reference errors out here, which can only happen when the FSE
        // form above unexpectedly loses).
        if (maxSymbolValue > 128)
        {
            return 0;
        }

        if (((maxSymbolValue + 1) / 2) + 1 > capacity)
        {
            return 0;
        }

        dst[offset] = (byte)(128 + (maxSymbolValue - 1));
        for (var i = 0; i < maxSymbolValue; i += 2)
        {
            var lo = i + 1 < maxSymbolValue ? weights[i + 1] : (byte)0;
            dst[offset + 1 + (i / 2)] = (byte)((weights[i] << 4) | lo);
        }

        return ((maxSymbolValue + 1) / 2) + 1;
    }

    // HUF_compressWeights: FSE-compresses the weight table (accuracy log ≤ 6).
    // Returns 0 when the weights are not worth FSE-compressing (caller uses
    // the direct form), 1 for a single repeated weight (same outcome).
    internal static int CompressWeights(byte[] dst, int offset, int capacity, byte[] weights, int weightCount)
    {
        if (weightCount <= 1)
        {
            return 0;
        }

        var count = new uint[TableLogMax + 1];
        for (var i = 0; i < weightCount; i++)
        {
            if (weights[i] > TableLogMax)
            {
                throw new ZstdException("Invalid Huffman weight.");
            }

            count[weights[i]]++;
        }

        var wmax = TableLogMax;
        while (wmax > 0 && count[wmax] == 0)
        {
            wmax--;
        }

        uint largest = 0;
        for (var i = 0; i <= wmax; i++)
        {
            largest = Math.Max(largest, count[i]);
        }

        if (largest == (uint)weightCount)
        {
            return 1;
        }

        if (largest == 1)
        {
            return 0;
        }

        try
        {
            var tableLog = ZstdFseEncoder.OptimalTableLog(MaxFseTableLogForHuffHeader, weightCount, wmax);
            var norm = new short[wmax + 1];
            var wcount = new uint[wmax + 1];
            Array.Copy(count, wcount, wmax + 1);
            if (ZstdFseEncoder.NormalizeCounts(norm, wcount, weightCount, wmax, tableLog, useLowProbCount: false) < 0)
            {
                return 0;
            }

            var hSize = ZstdFseEncoder.WriteNCount(dst, offset, capacity, norm, wmax, tableLog);
            var ct = ZstdFseEncoder.BuildCTable(norm, wmax, tableLog);
            var cSize = ZstdFseEncoder.Encode(dst, offset + hSize, capacity - hSize, weights, 0, weightCount, ct);
            if (cSize < 0)
            {
                return 0;
            }

            return hSize + cSize;
        }
        catch (ZstdException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Encodes one Huffman stream (<c>HUF_compress1X_usingCTable</c>): symbols
    /// back-to-front through a single top-anchored bit container, closed with
    /// the 1-bit end mark. Returns bytes written, or 0 when the stream does
    /// not fit (caller must store raw).
    /// </summary>
    public static int Compress1X(
        byte[] dst, int dstOffset, int dstCapacity,
        byte[] src, int srcOffset, int srcLength, HuffmanCTable table)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(table);

        if (srcLength <= 0 || dstCapacity < 8)
        {
            return 0;
        }

        var bitC = new HufCStream(dst, dstOffset, dstCapacity);
        for (var i = srcLength - 1; i >= 0; i--)
        {
            var symbol = src[srcOffset + i];
            int nbBits = table.NbBits[symbol];
            if (nbBits <= 0)
            {
                throw new ZstdException("Huffman symbol missing from table.");
            }

            if (bitC.BitPos > 64 - 12)
            {
                bitC.FlushBits();
            }

            bitC.AddBits(nbBits, table.Codes[symbol]);
        }

        return bitC.Close();
    }

    /// <summary>
    /// Encodes four Huffman streams with a 6-byte little-endian jump table
    /// (<c>HUF_compress4X_usingCTable</c>). Returns bytes written, or 0 when
    /// any stream does not fit or exceeds 65535 bytes (caller must store raw).
    /// </summary>
    public static int Compress4X(
        byte[] dst, int dstOffset, int dstCapacity,
        byte[] src, int srcOffset, int srcLength, HuffmanCTable table)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(table);

        if (dstCapacity < 6 + 1 + 1 + 1 + 8 || srcLength < FourStreamsMinInput)
        {
            return 0;
        }

        var segmentSize = (srcLength + 3) / 4;
        var op = dstOffset + 6;
        var capLeft = dstCapacity - 6;

        var c1 = Compress1X(dst, op, capLeft, src, srcOffset, segmentSize, table);
        if (c1 == 0 || c1 > 65535)
        {
            return 0;
        }

        WriteU16Le(dst, dstOffset, (ushort)c1);
        op += c1;
        capLeft -= c1;

        var c2 = Compress1X(dst, op, capLeft, src, srcOffset + segmentSize, segmentSize, table);
        if (c2 == 0 || c2 > 65535)
        {
            return 0;
        }

        WriteU16Le(dst, dstOffset + 2, (ushort)c2);
        op += c2;
        capLeft -= c2;

        var c3 = Compress1X(dst, op, capLeft, src, srcOffset + (2 * segmentSize), segmentSize, table);
        if (c3 == 0 || c3 > 65535)
        {
            return 0;
        }

        WriteU16Le(dst, dstOffset + 4, (ushort)c3);
        op += c3;

        var lastLength = srcLength - (3 * segmentSize);
        var c4 = Compress1X(dst, op, dstCapacity - (op - dstOffset), src, srcOffset + (3 * segmentSize), lastLength,
            table);
        if (c4 == 0 || c4 > 65535)
        {
            return 0;
        }

        op += c4;
        return op - dstOffset;
    }

    /// <summary>
    /// Compresses literals with a fresh Huffman table
    /// (<c>HUF_compress_internal</c> without table reuse: single-shot blocks
    /// have no previous table). Returns 0 when the input should be stored raw
    /// (empty, tiny, or incompressible), 1 for RLE (<c>dst[dstOffset]</c> holds
    /// the repeated byte), else the compressed size (table description plus
    /// streams, always ≥ 2 so it cannot collide with the RLE sentinel).
    /// </summary>
    public static int Compress(
        byte[] dst, int dstOffset, int dstCapacity,
        byte[] src, int srcOffset, int srcLength,
        int maxSymbolValue = SymbolValueMax, int huffLog = TableLogDefault)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);

        if (srcLength <= 0 || dstCapacity <= 0)
        {
            return 0;
        }

        if (srcLength > BlockSizeMax)
        {
            throw new ZstdException("Huffman input too large.");
        }

        if (maxSymbolValue == 0)
        {
            maxSymbolValue = SymbolValueMax;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSymbolValue, SymbolValueMax);

        if (huffLog == 0)
        {
            huffLog = TableLogDefault;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(huffLog, TableLogMax);

        // The decoder caps codes at 11 bits (RFC 8878 Section 4.2).
        huffLog = Math.Min(huffLog, TableLogDefault);

        var count = new uint[SymbolValueMax + 1];
        var end = srcOffset + srcLength;
        for (var i = srcOffset; i < end; i++)
        {
            count[src[i]]++;
        }

        var maxSv = maxSymbolValue;
        while (maxSv > 0 && count[maxSv] == 0)
        {
            maxSv--;
        }

        uint largest = 0;
        for (var i = 0; i <= maxSv; i++)
        {
            largest = Math.Max(largest, count[i]);
        }

        if (largest == (uint)srcLength)
        {
            dst[dstOffset] = src[srcOffset];
            return 1;
        }

        if (largest <= (uint)(srcLength >> 7) + 4)
        {
            return 0;
        }

        var tableLog = OptimalTableLog(srcLength, maxSv, huffLog);
        var (table, maxBits) = BuildCTable(count, maxSv, tableLog);
        huffLog = maxBits;

        var hSize = WriteCTable(dst, dstOffset, dstCapacity, table);
        if (hSize <= 0)
        {
            return 0;
        }

        if (hSize + 12 >= srcLength)
        {
            return 0;
        }

        var cSize = srcLength < SingleStreamThreshold
            ? Compress1X(dst, dstOffset + hSize, dstCapacity - hSize, src, srcOffset, srcLength, table)
            : Compress4X(dst, dstOffset + hSize, dstCapacity - hSize, src, srcOffset, srcLength, table);
        if (cSize == 0)
        {
            return 0;
        }

        var total = hSize + cSize;
        if (total >= srcLength - 1)
        {
            return 0;
        }

        return total;
    }

    private static void WriteU16Le(byte[] dst, int offset, ushort value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>Huffman tree-build node (count, parent link, symbol, and depth).</summary>
    // One Huffman tree node (nodeElt from lib/compress/huf_compress.c).
    [StructLayout(LayoutKind.Auto)]
    private struct NodeElt
    {
        /// <summary>Subtree frequency.</summary>
        public uint Count;

        /// <summary>Parent node index.</summary>
        public ushort Parent;

        /// <summary>Symbol for leaf nodes.</summary>
        public byte Symbol;

        /// <summary>Code length in bits.</summary>
        public byte NbBits;
    }

    /// <summary>Single-container Huffman bit writer (top-anchored codes, 1-bit end mark).</summary>
    // Single-container HUF_CStream_t (lib/compress/huf_compress.c): the
    // second container only breaks data dependencies for speed, so one
    // container emits byte-identical output. New codes shift in from the top;
    // flushes emit whole bytes from the top, little-endian.
    private sealed class HufCStream
    {
        private readonly byte[] _dst;
        private readonly int _start;
        private readonly int _capacity;
        private ulong _container;
        private int _ptr;
        private bool _overflow;

        /// <summary>Creates a writer over <paramref name="dst"/> at <paramref name="offset"/>.</summary>
        /// <param name="dst">Destination buffer.</param>
        /// <param name="offset">Start offset.</param>
        /// <param name="capacity">Writable capacity from <paramref name="offset"/>.</param>
        public HufCStream(byte[] dst, int offset, int capacity)
        {
            _dst = dst;
            _start = offset;
            _capacity = capacity;
            _ptr = offset;
        }

        /// <summary>Bits currently held in the container.</summary>
        public int BitPos { get; private set; }

        /// <summary>Shifts a Huffman code into the top of the container.</summary>
        /// <param name="nbBits">Code length in bits.</param>
        /// <param name="value">Canonical code value.</param>
        public void AddBits(int nbBits, uint value)
        {
            _container >>= nbBits;
            _container |= (ulong)value << (64 - nbBits);
            BitPos += nbBits;
        }

        /// <summary>Emits whole bytes from the top of the container, little-endian.</summary>
        public void FlushBits()
        {
            var bits = BitPos;
            var nbBytes = bits >> 3;
            var tmp = _container >> (64 - bits);
            if (_ptr + nbBytes > _start + _capacity)
            {
                _overflow = true;
                return;
            }

            for (var i = 0; i < nbBytes; i++)
            {
                _dst[_ptr++] = (byte)(tmp >> (8 * i));
            }

            BitPos &= 7;
        }

        /// <summary>Appends the 1-bit end mark, flushes, and returns bytes written (0 on overflow).</summary>
        /// <returns>Bytes written, or 0 when the stream did not fit.</returns>
        public int Close()
        {
            AddBits(1, 1); // End mark: a 1-bit value of 1.
            FlushBits();
            if (_overflow)
            {
                return 0;
            }

            var rest = BitPos;
            if (rest > 0)
            {
                if (_ptr + 1 > _start + _capacity)
                {
                    return 0;
                }

                _dst[_ptr++] = (byte)(_container >> (64 - rest));
            }

            return _ptr - _start;
        }
    }
}