namespace ZARSharp.Zstd;

/// <summary>
/// Backward (LIFO) little-endian bit reader (RFC 8878 Sections 4.1, 4.2.2).
/// Bits are read from the end of the buffer toward the beginning;
/// multi-bit fields come out little-endian. Reads past the useful region
/// throw <see cref="ZstdException"/> (corrupt input); the FSE-weights loop
/// checks <see cref="RemainingBits"/> explicitly instead (see RFC 4.2.1.2).
/// </summary>
internal sealed class BackwardBitReader
{
    private readonly byte[] _buf;
    private readonly int _offset;
    private long _pos; // exclusive top: next read takes bits below _pos

    private BackwardBitReader(byte[] buf, int offset, long useBits)
    {
        _buf = buf;
        _offset = offset;
        _pos = useBits;
    }

    /// <summary>Useful bits not yet consumed.</summary>
    public long RemainingBits => _pos;

    /// <summary>True when the stream is exactly consumed.</summary>
    public bool IsAtEnd => _pos == 0;

    /// <summary>
    /// Initializes over a Huffman-coded stream: the highest set bit of the
    /// last byte is the end mark (RFC 8878 Section 4.2.2).
    /// </summary>
    public static BackwardBitReader ForHuffmanStream(byte[] buf, int offset, int length)
    {
        if (length <= 0)
        {
            throw new ZstdException("Empty Huffman stream.");
        }

        byte last = buf[offset + length - 1];
        if (last == 0)
        {
            throw new ZstdException("Huffman stream end mark missing.");
        }

        return new BackwardBitReader(buf, offset, ((long)length - 1) * 8 + HighestBit(last));
    }

    /// <summary>
    /// Initializes over a sequences bitstream: skips zero padding and the
    /// single end-mark bit (RFC 8878 Section 3.1.1.3.2.1.2).
    /// </summary>
    public static BackwardBitReader ForSequenceStream(byte[] buf, int offset, int length)
    {
        long totalBits = (long)length * 8;
        long p = totalBits;
        while (p > 0 && GetBit(buf, offset, p - 1) == 0)
        {
            p--;
        }

        if (p == 0)
        {
            throw new ZstdException("Sequence bitstream end mark missing.");
        }

        return new BackwardBitReader(buf, offset, p - 1);
    }

    private static int GetBit(byte[] data, int offset, long bitIndex)
    {
        return (data[offset + (bitIndex >> 3)] >> (int)(bitIndex & 7)) & 1;
    }

    private static int HighestBit(byte v)
    {
        int h = 0;
        while (v > 1)
        {
            v >>= 1;
            h++;
        }

        return h;
    }

    /// <summary>Reads <paramref name="count"/> bits (0-32) little-endian.</summary>
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (count < 0 || count > 32)
        {
            throw new ZstdException("Invalid bit read size.");
        }

        if (_pos < count)
        {
            throw new ZstdException("Truncated bitstream.");
        }

        _pos -= count;
        long baseBit = _pos;
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            long p = baseBit + i;
            int bit = (_buf[_offset + (p >> 3)] >> (int)(p & 7)) & 1;
            value |= (uint)(bit << i);
        }

        return value;
    }
}

/// <summary>
/// Forward little-endian bit reader (FSE table descriptions). Reads past the
/// end return zero, mirroring the reference (which zero-pads short inputs);
/// callers must validate the consumed byte count.
/// </summary>
internal sealed class ForwardBitReader
{
    private readonly byte[] _buf;
    private readonly int _start;
    private readonly int _end;
    private long _bitPos; // absolute bit position of next bit to read

    /// <summary>Creates a reader over <c>buf[offset..offset+length)</c>.</summary>
    public ForwardBitReader(byte[] buf, int offset, int length)
    {
        _buf = buf;
        _start = offset;
        _bitPos = (long)offset * 8;
        _end = offset + length;
    }

    /// <summary>Bits consumed so far (relative to the stream start).</summary>
    public long ConsumedBits => _bitPos - (long)_start * 8;

    /// <summary>Peeks <paramref name="count"/> bits (0-32) without advancing.</summary>
    public uint PeekBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            long p = _bitPos + i;
            int bit = 0;
            if (p < (long)_end * 8 && p >= 0 && (p >> 3) < _buf.Length)
            {
                bit = (_buf[p >> 3] >> (int)(p & 7)) & 1;
            }

            value |= (uint)(bit << i);
        }

        return value;
    }

    /// <summary>Reads <paramref name="count"/> bits (0-32) little-endian.</summary>
    public uint ReadBits(int count)
    {
        uint value = PeekBits(count);
        _bitPos += count;
        return value;
    }
}