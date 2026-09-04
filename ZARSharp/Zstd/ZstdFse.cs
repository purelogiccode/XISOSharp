using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// Finite State Entropy tables: normalized-count parsing
/// (<c>FSE_readNCount</c>) and decoding-table construction
/// (<c>FSE_buildDTable</c>). Behavior matches the reference exactly;
/// see RFC 8878 Section 4.1.
/// </summary>
internal static class ZstdFse
{
    /// <summary>Generic FSE decoding table.</summary>
    public sealed class DecodeTable
    {
        /// <summary>Accuracy log (table size = 1 &lt;&lt; TableLog).</summary>
        public int TableLog;

        /// <summary>Symbol per state.</summary>
        public int[] Symbols = [];

        /// <summary>Bits to read per state.</summary>
        public byte[] NumBits = [];

        /// <summary>Baseline for the next state per state.</summary>
        public int[] NewState = [];
    }

    private static int Highbit(uint v)
    {
        if (v == 0)
        {
            throw new ZstdException("Invalid FSE table (zero total).");
        }

        return BitOperations.Log2(v);
    }

    /// <summary>
    /// Parses an FSE distribution header. <paramref name="maxSymbol"/> is the
    /// maximum allowed symbol value (inclusive). Returns bytes consumed.
    /// </summary>
    public static int ParseNormalizedCounts(
        byte[] buf, int offset, int length, int maxSymbol,
        out short[] norms, out int tableLog, out int maxSymbolValue)
    {
        var reader = new ForwardBitReader(buf, offset, length);
        var first = reader.ReadBits(4);
        tableLog = (int)first + 5;
        if (tableLog > 15)
        {
            throw new ZstdException("FSE tableLog too large.");
        }

        norms = new short[maxSymbol + 1];
        var remaining = (1 << tableLog) + 1;
        var threshold = 1 << tableLog;
        var nbBits = tableLog + 1;
        var charnum = 0;
        var maxSv1 = maxSymbol + 1;
        var previous0 = false;

        for (;;)
        {
            if (previous0)
            {
                // Count zero repeats: each 0b11 continues.
                var repeats = 0;
                while (reader.PeekBits(2) == 3)
                {
                    reader.ReadBits(2);
                    repeats++;
                }

                var last = reader.ReadBits(2);
                charnum += (repeats * 3) + (int)last;
                if (charnum >= maxSv1)
                {
                    break;
                }

                previous0 = false; // zeros are already 0 in norms
                continue;
            }

            var max = (2 * threshold) - 1 - remaining;
            int count;
            if ((int)reader.PeekBits(nbBits - 1) < max)
            {
                count = (int)reader.ReadBits(nbBits - 1);
            }
            else
            {
                count = (int)reader.ReadBits(nbBits);
                if (count >= threshold)
                {
                    count -= max;
                }
            }

            count--; // P = value - 1; -1 means "less than 1"
            if (count >= 0)
            {
                remaining -= count;
            }
            else
            {
                remaining--; // -1 counts as 1 point (reference: remaining += count)
            }

            norms[charnum++] = (short)count;
            previous0 = count == 0;

            if (remaining < threshold)
            {
                if (remaining <= 1)
                {
                    break;
                }

                nbBits = Highbit((uint)remaining) + 1;
                threshold = 1 << (nbBits - 1);
            }

            if (charnum >= maxSv1)
            {
                break;
            }
        }

        if (remaining != 1)
        {
            throw new ZstdException("Corrupt FSE distribution.");
        }

        if (charnum > maxSv1)
        {
            throw new ZstdException("FSE symbol value too large.");
        }

        maxSymbolValue = charnum - 1;
        var consumedBytes = (int)((reader.ConsumedBits + 7) >> 3);
        if (consumedBytes > length)
        {
            throw new ZstdException("Truncated FSE table description.");
        }

        return consumedBytes;
    }

    /// <summary>Builds a generic decoding table from normalized counts.</summary>
    public static DecodeTable BuildTable(short[] norms, int maxSymbolValue, int tableLog)
    {
        if (tableLog < 1 || tableLog > 15)
        {
            throw new ZstdException("Invalid FSE tableLog.");
        }

        var tableSize = 1 << tableLog;
        var tableMask = tableSize - 1;
        var step = (tableSize >> 1) + (tableSize >> 3) + 3;

        // Sanity: probabilities must sum to exactly tableSize (-1 counts 1).
        long total = 0;
        for (var s = 0; s <= maxSymbolValue; s++)
        {
            total += norms[s] == -1 ? 1 : norms[s];
            if (norms[s] < -1)
            {
                throw new ZstdException("Corrupt FSE distribution.");
            }
        }

        if (total != tableSize)
        {
            throw new ZstdException("Corrupt FSE distribution.");
        }

        var tableSymbol = new int[tableSize];
        var symbolNext = new int[maxSymbolValue + 1];
        var highThreshold = tableSize - 1;

        // Low-probability ("less than 1") symbols get single cells from the top.
        for (var s = 0; s <= maxSymbolValue; s++)
        {
            if (norms[s] == -1)
            {
                tableSymbol[highThreshold--] = s;
                symbolNext[s] = 1;
            }
            else
            {
                symbolNext[s] = norms[s];
            }
        }

        // Spread remaining symbols.
        var position = 0;
        for (var s = 0; s <= maxSymbolValue; s++)
        {
            int n = norms[s];
            for (var i = 0; i < n; i++)
            {
                tableSymbol[position] = s;
                position = (position + step) & tableMask;
                while (position > highThreshold)
                {
                    position = (position + step) & tableMask;
                }
            }
        }

        if (position != 0)
        {
            throw new ZstdException("Corrupt FSE distribution.");
        }

        var table = new DecodeTable
        {
            TableLog = tableLog,
            Symbols = new int[tableSize],
            NumBits = new byte[tableSize],
            NewState = new int[tableSize],
        };

        for (var u = 0; u < tableSize; u++)
        {
            var symbol = tableSymbol[u];
            var nextState = symbolNext[symbol]++;
            var nbBits = tableLog - Highbit((uint)nextState);
            table.Symbols[u] = symbol;
            table.NumBits[u] = (byte)nbBits;
            table.NewState[u] = (nextState << nbBits) - tableSize;
        }

        return table;
    }
}