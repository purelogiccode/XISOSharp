namespace ZARSharp.Zstd;

/// <summary>
/// Huffman tables and Huffman-coded stream decoding (RFC 8878 Section 4.2).
/// Uses single-symbol lookups (equivalent to the reference X2 tables, which
/// additionally precompute symbol pairs as a speed optimization).
/// </summary>
internal static class ZstdHuffman
{
    /// <summary>Canonical Huffman decoding table.</summary>
    public sealed class HuffmanTable
    {
        /// <summary>Maximum code length (table size = 1 &lt;&lt; MaxBits, ≤ 11).</summary>
        public int MaxBits;

        /// <summary>Symbol per table entry.</summary>
        public byte[] Symbols = [];

        /// <summary>Code length per table entry.</summary>
        public byte[] NumBits = [];
    }

    /// <summary>
    /// Reads a Huffman tree description (<c>HUF_readStats</c>). Returns bytes
    /// consumed; outputs the full weight list including the implied last
    /// weight, the table log and the symbol count.
    /// </summary>
    public static int ReadStats(
        byte[] buf, int offset, int length,
        out byte[] weights, out int tableLog, out int numSymbols)
    {
        if (length <= 0)
        {
            throw new ZstdException("Truncated Huffman tree description.");
        }

        int header = buf[offset];
        byte[] transmitted;
        int transmittedCount;
        int headerSize;
        if (header >= 128)
        {
            // Direct nibble-packed weights.
            transmittedCount = header - 127;
            if (transmittedCount > 128)
            {
                throw new ZstdException("Invalid Huffman direct weight count.");
            }

            var packed = (transmittedCount + 1) / 2;
            if (packed + 1 > length)
            {
                throw new ZstdException("Truncated Huffman weights.");
            }

            transmitted = new byte[transmittedCount];
            for (var n = 0; n < transmittedCount; n += 2)
            {
                var b = buf[offset + 1 + (n / 2)];
                transmitted[n] = (byte)(b >> 4);
                if (n + 1 < transmittedCount)
                {
                    transmitted[n + 1] = (byte)(b & 0xF);
                }
            }

            headerSize = packed + 1;
        }
        else
        {
            // FSE-compressed weights (accuracy log ≤ 6, max 255 weights).
            var fseSize = header;
            if (fseSize + 1 > length)
            {
                throw new ZstdException("Truncated Huffman weights.");
            }

            var consumed = ZstdFse.ParseNormalizedCounts(
                buf, offset + 1, fseSize, 255,
                out var norms, out var fseLog, out var fseMaxSymbol);
            if (fseLog > 6)
            {
                throw new ZstdException("Huffman weight tableLog too large.");
            }

            var table = ZstdFse.BuildTable(norms, fseMaxSymbol, fseLog);
            transmitted = DecompressWeightsFse(buf, offset + 1 + consumed, fseSize - consumed, table, 255);
            transmittedCount = transmitted.Length;
            headerSize = fseSize + 1;
        }

        // Validate and deduce the implied last weight (reference HUF_readStats).
        var rankStats = new int[13];
        long weightTotal = 0;
        for (var n = 0; n < transmittedCount; n++)
        {
            int w = transmitted[n];
            if (w > 12)
            {
                throw new ZstdException("Invalid Huffman weight.");
            }

            rankStats[w]++;
            weightTotal += (1 << w) >> 1;
        }

        if (weightTotal == 0)
        {
            throw new ZstdException("Invalid Huffman weights.");
        }

        var maxBits = Highbit((uint)weightTotal) + 1;
        if (maxBits > 12)
        {
            throw new ZstdException("Huffman tree too deep.");
        }

        // Note: HUF_TABLELOG_MAX is 12 here, but the format caps codes at 11
        // bits (RFC 8878 Section 4.2); enforce below after the last weight.
        var rest = (1u << maxBits) - (uint)weightTotal;
        if (rest == 0 || (rest & (rest - 1)) != 0)
        {
            throw new ZstdException("Invalid Huffman weights.");
        }

        var lastWeight = Highbit(rest) + 1;
        weights = new byte[transmittedCount + 1];
        Array.Copy(transmitted, weights, transmittedCount);
        weights[transmittedCount] = (byte)lastWeight;
        numSymbols = transmittedCount + 1;
        if (lastWeight > 12)
        {
            throw new ZstdException("Invalid Huffman weight.");
        }

        rankStats[lastWeight]++;
        if (rankStats[1] < 2 || (rankStats[1] & 1) != 0)
        {
            throw new ZstdException("Invalid Huffman tree.");
        }

        // Maximum code length is 11 bits (Max_Number_of_Bits ≤ 11).
        for (var n = 0; n < numSymbols; n++)
        {
            if (weights[n] != 0 && maxBits + 1 - weights[n] > 11)
            {
                throw new ZstdException("Huffman tree too deep.");
            }
        }

        tableLog = maxBits;
        return headerSize;
    }

    private static int Highbit(uint v)
    {
        if (v == 0)
        {
            throw new ZstdException("Invalid Huffman weights.");
        }

        return System.Numerics.BitOperations.Log2(v);
    }

    /// <summary>
    /// FSE-decodes Huffman weights: two interleaved states sharing one
    /// table (RFC 8878 Section 4.2.1.2). Termination follows the RFC rule:
    /// when an update would need more bits than remain, the extra bits are
    /// zero (the update is then discarded) and one final symbol is decoded
    /// from the other state.
    /// </summary>
    private static byte[] DecompressWeightsFse(
        byte[] buf, int offset, int length, ZstdFse.DecodeTable table, int maxOut)
    {
        var bitD = BackwardBitReader.ForHuffmanStream(buf, offset, length);
        var log = table.TableLog;
        if (bitD.RemainingBits < log)
        {
            throw new ZstdException("Truncated Huffman weights.");
        }

        var state1 = (int)bitD.ReadBits(log);
        if (bitD.RemainingBits < log)
        {
            throw new ZstdException("Truncated Huffman weights.");
        }

        var state2 = (int)bitD.ReadBits(log);

        var outWeights = new List<byte>(maxOut);
        while (true)
        {
            if (outWeights.Count >= maxOut)
            {
                throw new ZstdException("Too many Huffman weights.");
            }

            outWeights.Add((byte)table.Symbols[state1]);
            int need = table.NumBits[state1];
            if (need > bitD.RemainingBits)
            {
                if (outWeights.Count >= maxOut)
                {
                    throw new ZstdException("Too many Huffman weights.");
                }

                outWeights.Add((byte)table.Symbols[state2]);
                break;
            }

            state1 = table.NewState[state1] + (int)bitD.ReadBits(need);

            if (outWeights.Count >= maxOut)
            {
                throw new ZstdException("Too many Huffman weights.");
            }

            outWeights.Add((byte)table.Symbols[state2]);
            need = table.NumBits[state2];
            if (need > bitD.RemainingBits)
            {
                if (outWeights.Count >= maxOut)
                {
                    throw new ZstdException("Too many Huffman weights.");
                }

                outWeights.Add((byte)table.Symbols[state1]);
                break;
            }

            state2 = table.NewState[state2] + (int)bitD.ReadBits(need);
        }

        return [.. outWeights];
    }

    /// <summary>Builds a canonical decoding table from weights.</summary>
    public static HuffmanTable BuildTable(byte[] weights, int numSymbols, int maxBits)
    {
        if (maxBits < 1 || maxBits > 11)
        {
            throw new ZstdException("Invalid Huffman table depth.");
        }

        var tableSize = 1 << maxBits;
        var table = new HuffmanTable
        {
            MaxBits = maxBits,
            Symbols = new byte[tableSize],
            NumBits = new byte[tableSize],
        };

        // Ascending weight (descending length), ascending symbol (RFC 4.2.1.3).
        var code = 0;
        for (var w = 1; w <= maxBits; w++)
        {
            var nbBits = maxBits + 1 - w;
            for (var s = 0; s < numSymbols; s++)
            {
                if (weights[s] == w)
                {
                    var reps = 1 << (maxBits - nbBits);
                    if (code + reps > tableSize)
                    {
                        throw new ZstdException("Invalid Huffman tree.");
                    }

                    for (var k = 0; k < reps; k++)
                    {
                        table.Symbols[code + k] = (byte)s;
                        table.NumBits[code + k] = (byte)nbBits;
                    }

                    code += reps;
                }
            }
        }

        if (code != tableSize)
        {
            throw new ZstdException("Invalid Huffman tree.");
        }

        return table;
    }

    /// <summary>
    /// Peeks the next <paramref name="maxBits"/>-bit table index: the
    /// next-to-read bit is the most significant bit (matches reference
    /// <c>BIT_lookBitsFast</c>). Real remaining bits occupy the HIGH
    /// positions; missing low positions read as zero, which is harmless
    /// because entries replicate over the low bits.
    /// </summary>
    private static uint PeekHuffman(BackwardBitReader bitD, byte[] buf, int streamOffset, int maxBits)
    {
        var remaining = bitD.RemainingBits;
        if (remaining >= maxBits)
        {
            var baseBit = remaining - maxBits;
            uint value = 0;
            for (var i = 0; i < maxBits; i++)
            {
                var p = baseBit + i;
                var bit = (buf[streamOffset + (p >> 3)] >> (int)(p & 7)) & 1;
                value |= (uint)(bit << i);
            }

            return value;
        }

        uint low = 0;
        for (var i = 0; i < remaining; i++)
        {
            var bit = (buf[streamOffset + (i >> 3)] >> (int)(i & 7)) & 1;
            low |= (uint)(bit << i);
        }

        return low << (maxBits - (int)remaining);
    }

    /// <summary>
    /// Decodes one Huffman-coded stream to <c>dst[dstOffset..dstOffset+dstLength)</c>.
    /// The stream must be exactly consumed.
    /// </summary>
    public static void DecodeStream(
        byte[] buf, int offset, int length,
        HuffmanTable table, byte[] dst, int dstOffset, int dstLength)
    {
        var bitD = BackwardBitReader.ForHuffmanStream(buf, offset, length);
        var maxBits = table.MaxBits;
        var end = dstOffset + dstLength;
        var p = dstOffset;
        while (p < end)
        {
            var index = PeekHuffman(bitD, buf, offset, maxBits);
            var sym = table.Symbols[index];
            int len = table.NumBits[index];
            if (len > bitD.RemainingBits)
            {
                throw new ZstdException("Truncated Huffman stream.");
            }

            bitD.ReadBits(len);
            dst[p++] = sym;
        }

        if (!bitD.IsAtEnd)
        {
            throw new ZstdException("Huffman stream not exactly consumed.");
        }
    }
}