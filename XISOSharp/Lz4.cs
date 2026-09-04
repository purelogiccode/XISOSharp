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
    public static int MaxCompressedOutputSize(int inputLength) => 16 + 4 + (inputLength * 110 / 100);

    /// <summary>
    /// Compresses <paramref name="input"/> into <paramref name="destination"/> as a raw LZ4 block.
    /// Returns the number of bytes written. <paramref name="acceleration"/> = 1 matches
    /// <c>lz4_flex</c> byte for byte; values &gt; 1 trade ratio for speed.
    /// </summary>
    public static int Compress(ReadOnlySpan<byte> input, Span<byte> destination, int acceleration = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceleration, 1);
        var required = MaxCompressedOutputSize(input.Length);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination is too small ({destination.Length} bytes); at least {required} required",
                nameof(destination));
        }

        var outPos = 0;
        if (input.Length < MinLength)
        {
            WriteLastLiterals(input, destination, ref outPos, 0);
            return outPos;
        }

        var dict = new int[HashTableSize];
        var endPosCheck = input.Length - MfLimit;
        var literalStart = 0;
        var cur = 0;

        // lz4_flex: a block cannot start with a match, so seed the table with position 0.
        dict[GetHashTableIndex(input, 0)] = 0;
        cur = 1;

        while (true)
        {
            var nonMatchCount = acceleration << IncreaseStepSizeBitShift;
            var nextCur = cur;
            int candidate;
            int offset;

            // Search for a duplicate via the hash table, increasing the step after
            // 1 << IncreaseStepSizeBitShift non-matches.
            while (true)
            {
                var stepSize = nonMatchCount >> IncreaseStepSizeBitShift;
                nonMatchCount++;

                cur = nextCur;
                nextCur += stepSize;

                if (cur > endPosCheck)
                {
                    WriteLastLiterals(input, destination, ref outPos, literalStart);
                    return outPos;
                }

                var hash = GetHashTableIndex(input, cur);
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

            var litLen = cur - literalStart;
            cur += MinMatch;
            candidate += MinMatch;
            var dupLen = CountMatchBytes(input, ref cur, candidate);

            dict[GetHashTableIndex(input, cur - 2)] = cur - 2;

            var token = (byte)((Math.Min(litLen, 0xF) << 4) | Math.Min(dupLen, 0xF));
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
        var srcPos = 0;
        var dstPos = 0;

        while (srcPos < source.Length)
        {
            var token = source[srcPos++];
            var litLen = token >> 4;
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
            var offset = source[srcPos] | (source[srcPos + 1] << 8);
            srcPos += 2;
            if (offset == 0)
                throw new InvalidDataException("LZ4: match offset is zero");

            var matchLen = token & 0xF;
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
            var matchPos = dstPos - offset;
            if (offset >= matchLen)
            {
                destination.Slice(matchPos, matchLen).CopyTo(destination.Slice(dstPos));
            }
            else
            {
                for (var i = 0; i < matchLen; i++)
                    destination[dstPos + i] = destination[matchPos + i];
            }

            dstPos += matchLen;
        }

        return dstPos;
    }

    private static int GetHashTableIndex(ReadOnlySpan<byte> input, int pos)
    {
        // lz4_flex hash5: (read_u64(pos) << 24 * prime) >> 48, table index = hash >> 4.
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(pos, 8));
        var hash = (uint)(((seq << 24) * Prime5Bytes) >> 48);
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
        var litLen = input.Length - start;
        destination[pos++] = litLen < 0xF ? (byte)(litLen << 4) : (byte)0xF0;
        if (litLen >= 0xF)
            WriteInteger(destination, ref pos, litLen - 0xF);

        input.Slice(start).CopyTo(destination.Slice(pos));
        pos += litLen;
    }

    private static int CountMatchBytes(ReadOnlySpan<byte> input, ref int cur, int candidate)
    {
        var start = cur;
        var maxInputMatch = Math.Max(0, input.Length - (cur + EndOffset));
        var maxCandidateMatch = input.Length - candidate;
        var inputEnd = cur + Math.Min(maxInputMatch, maxCandidateMatch);

        while (cur + 8 <= inputEnd)
        {
            var diff = BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(cur, 8)) ^
                       BinaryPrimitives.ReadUInt64LittleEndian(input.Slice(candidate, 8));
            if (diff == 0)
            {
                cur += 8;
                candidate += 8;
            }
            else
            {
                cur += (int)(BitOperations.TrailingZeroCount(diff) / 8);
                return cur - start;
            }
        }

        if (inputEnd - cur >= 4)
        {
            var diff = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(cur, 4)) ^
                       BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(candidate, 4));
            if (diff == 0)
            {
                cur += 4;
                candidate += 4;
            }
            else
            {
                cur += (int)(BitOperations.TrailingZeroCount(diff) / 8);
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