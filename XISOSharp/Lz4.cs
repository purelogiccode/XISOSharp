using System.Buffers.Binary;
using System.Numerics;

namespace XISOSharp;

/// <summary>
/// Pure-managed LZ4 block codec. <see cref="Compress"/> is a byte-exact port of
/// <c>lz4_flex 0.11.3</c> block compression (<c>src/block/compress.rs</c> + <c>src/block/hashtable.rs</c>,
/// 64-bit <c>hash5</c> variant) — the encoder used by the <c>ciso</c> crate 0.2 inside xdvdfs for
/// CISO v2 sector payloads. With <c>acceleration = 1</c> (the default) output is byte-identical to
/// <c>lz4_flex::block::compress</c>, i.e. to what modern <c>xdvdfs compress</c> produces; higher
/// accelerations grow the search step sooner (faster, larger output, still spec-valid LZ4).
/// <see cref="Decompress"/> implements the LZ4 block specification
/// (https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md) and decodes any conforming block.
/// No external dependencies, keeping <c>IsTrimmable</c>/<c>IsAotCompatible</c> true.
/// </summary>
public static class Lz4
{
    private const int MinMatch = 4;
    private const int MfLimit = 12;
    private const int LastLiterals = 5;
    private const int EndOffset = LastLiterals + 1; // 6 — matches kept this far from the end of input
    private const int MinLength = MfLimit + 1; // 13
    private const int MaxDistance = 0xFFFF;
    private const int IncreaseStepSizeBitShift = 5;
    private const int HashTableSize = 4 * 1024;
    private const int HashTableBitShift = 4;
    private const ulong Prime5Bytes = 889523592379UL; // lz4_flex hash5 prime (little-endian)

    /// <summary>
    /// Returns the minimum destination size <see cref="Compress"/> requires for
    /// <paramref name="inputLength"/> input bytes (<c>16 + 4 + inputLength * 110 / 100</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="inputLength"/> is negative, or the required
    /// size exceeds <see cref="int.MaxValue"/> (BUG-LIB-032: the old
    /// <c>inputLength * 110 / 100</c> overflowed <c>int</c> for ~20 MB+ inputs,
    /// yielding a negative required size).
    /// </exception>
    public static int MaxCompressedOutputSize(int inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        long required = 20L + ((long)inputLength * 110 / 100);
        if (required > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength), inputLength,
                $"Required output size {required} exceeds the maximum addressable length.");
        }

        return (int)required;
    }

    /// <summary>
    /// Compresses <paramref name="input"/> into <paramref name="destination"/> as a raw LZ4 block.
    /// Returns the number of bytes written. <paramref name="acceleration"/> = 1 matches
    /// <c>lz4_flex</c> byte for byte; values &gt; 1 trade ratio for speed.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> destination, int acceleration = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceleration, 1);
        int required = MaxCompressedOutputSize(input.Length);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination is too small ({destination.Length} bytes); at least {required} required",
                nameof(destination));
        }

        int outPos = 0;
        if (input.Length < MinLength)
        {
            WriteLastLiterals(input, destination, ref outPos, 0);
            return outPos;
        }

        int[] dict = new int[HashTableSize];
        int endPosCheck = input.Length - MfLimit;
        int literalStart = 0;
        int cur = 0;

        // lz4_flex: a block cannot start with a match, so seed the table with position 0.
        dict[GetHashTableIndex(input, 0)] = 0;
        cur = 1;

        while (true)
        {
            int nonMatchCount = acceleration << IncreaseStepSizeBitShift;
            int nextCur = cur;
            int candidate;
            int offset;

            // Search for a duplicate via the hash table, increasing the step after
            // 1 << IncreaseStepSizeBitShift non-matches.
            while (true)
            {
                int stepSize = nonMatchCount >> IncreaseStepSizeBitShift;
                nonMatchCount++;

                cur = nextCur;
                nextCur += stepSize;

                if (cur > endPosCheck)
                {
                    WriteLastLiterals(input, destination, ref outPos, literalStart);
                    return outPos;
                }

                int hash = GetHashTableIndex(input, cur);
                candidate = dict[hash];
                dict[hash] = cur;

                // Matches can address at most 16 bits of offset.
                if (cur - candidate > MaxDistance)
                    continue;

                if (BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(candidate, MinMatch)) ==
                    BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(cur, MinMatch)))
                {
                    offset = cur - candidate;
                    break;
                }
            }

            // Extend the match backwards while the bytes match.
            while (candidate > 0 && cur > literalStart && input[cur - 1] == input[candidate - 1])
            {
                cur--;
                candidate--;
            }

            int litLen = cur - literalStart;
            cur += MinMatch;
            candidate += MinMatch;
            int dupLen = CountMatchBytes(input, ref cur, candidate);

            dict[GetHashTableIndex(input, cur - 2)] = cur - 2;

            byte token = (byte)((Math.Min(litLen, 0xF) << 4) | Math.Min(dupLen, 0xF));
            destination[outPos++] = token;
            if (litLen >= 0xF)
                WriteInteger(destination, ref outPos, litLen - 0xF);

            input.Slice(literalStart, litLen).CopyTo(destination.Slice(outPos));
            outPos += litLen;

            destination[outPos++] = (byte)offset;
            destination[outPos++] = (byte)(offset >> 8);

            if (dupLen >= 0xF)
                WriteInteger(destination, ref outPos, dupLen - 0xF);

            literalStart = cur;
        }
    }

    /// <summary>
    /// Decompresses a raw LZ4 block from <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the number of bytes written. Throws <see cref="InvalidDataException"/> on malformed input.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int srcPos = 0;
        int dstPos = 0;

        while (srcPos < source.Length)
        {
            byte token = source[srcPos++];
            int litLen = token >> 4;
            if (litLen == 0xF)
            {
                byte add;
                do
                {
                    if (srcPos >= source.Length)
                        throw new InvalidDataException("LZ4: truncated literal length");
                    add = source[srcPos++];
                    litLen += add;
                } while (add == 0xFF);
            }

            if (srcPos + litLen > source.Length || dstPos + litLen > destination.Length)
                throw new InvalidDataException("LZ4: literal out of bounds");
            source.Slice(srcPos, litLen).CopyTo(destination.Slice(dstPos));
            srcPos += litLen;
            dstPos += litLen;

            // The last sequence contains only literals.
            if (srcPos >= source.Length)
                break;

            if (srcPos + 2 > source.Length)
                throw new InvalidDataException("LZ4: truncated match offset");
            int offset = source[srcPos] | (source[srcPos + 1] << 8);
            srcPos += 2;
            if (offset == 0)
                throw new InvalidDataException("LZ4: match offset is zero");

            int matchLen = token & 0xF;
            if (matchLen == 0xF)
            {
                byte add;
                do
                {
                    if (srcPos >= source.Length)
                        throw new InvalidDataException("LZ4: truncated match length");
                    add = source[srcPos++];
                    matchLen += add;
                } while (add == 0xFF);
            }

            matchLen += MinMatch;
            if (offset > dstPos)
                throw new InvalidDataException("LZ4: match offset out of range");
            if (matchLen > destination.Length - dstPos)
                throw new InvalidDataException("LZ4: match overruns destination");

            // Overlap-safe copy.
            int matchPos = dstPos - offset;
            if (offset >= matchLen)
            {
                destination.Slice(matchPos, matchLen).CopyTo(destination.Slice(dstPos));
            }
            else
            {
                for (int i = 0; i < matchLen; i++)
                    destination[dstPos + i] = destination[matchPos + i];
            }

            dstPos += matchLen;
        }

        return dstPos;
    }

    private static int GetHashTableIndex(ReadOnlySpan<byte> input, int pos)
    {
        // lz4_flex hash5: (read_u64(pos) << 24 * prime) >> 48, table index = hash >> 4.
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(pos, 8));
        uint hash = (uint)(((seq << 24) * Prime5Bytes) >> 48);
        return (int)(hash >> HashTableBitShift);
    }

    private static void WriteInteger(Span<byte> destination, ref int pos, int n)
    {
        while (n >= 0xFF)
        {
            destination[pos++] = 0xFF;
            n -= 0xFF;
        }

        destination[pos++] = (byte)n;
    }

    private static void WriteLastLiterals(ReadOnlySpan<byte> input, Span<byte> destination, ref int pos, int start)
    {
        int litLen = input.Length - start;
        destination[pos++] = litLen < 0xF ? (byte)(litLen << 4) : (byte)0xF0;
        if (litLen >= 0xF)
            WriteInteger(destination, ref pos, litLen - 0xF);

        input.Slice(start).CopyTo(destination.Slice(pos));
        pos += litLen;
    }

    private static int CountMatchBytes(ReadOnlySpan<byte> input, ref int cur, int candidate)
    {
        int start = cur;
        int maxInputMatch = Math.Max(0, input.Length - (cur + EndOffset));
        int maxCandidateMatch = input.Length - candidate;
        int inputEnd = cur + Math.Min(maxInputMatch, maxCandidateMatch);

        while (cur + 8 <= inputEnd)
        {
            ulong diff = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(cur, 8)) ^
                         BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(candidate, 8));
            if (diff == 0)
            {
                cur += 8;
                candidate += 8;
            }
            else
            {
                cur += BitOperations.TrailingZeroCount(diff) / 8;
                return cur - start;
            }
        }

        if (inputEnd - cur >= 4)
        {
            uint diff = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(cur, 4)) ^
                        BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(candidate, 4));
            if (diff == 0)
            {
                cur += 4;
                candidate += 4;
            }
            else
            {
                cur += BitOperations.TrailingZeroCount(diff) / 8;
                return cur - start;
            }
        }

        if (inputEnd - cur >= 2 &&
            BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(cur, 2)) ==
            BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(candidate, 2)))
        {
            cur += 2;
            candidate += 2;
        }

        if (cur < inputEnd && input[cur] == input[candidate])
            cur++;

        return cur - start;
    }
}
