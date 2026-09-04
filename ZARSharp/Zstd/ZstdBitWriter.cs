namespace ZARSharp.Zstd;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// LSB-first forward bit writer (inverse of <see cref="ForwardBitReader"/>).
/// Used for FSE NCount headers (<c>FSE_writeNCount</c>), which have no end-mark:
/// bits accumulate LSB-first and <see cref="Flush"/> pads the last byte with zeros.
/// </summary>
internal sealed class ForwardBitWriter
{
    private readonly byte[] _buf;
    private readonly int _start;
    private readonly int _capacity;
    private int _pos;
    private uint _bitBuf;

    /// <summary>Creates a writer over <c>buf[offset..offset+capacity)</c>.</summary>
    public ForwardBitWriter(byte[] buf, int offset, int capacity)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (offset + capacity > buf.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buf = buf;
        _start = offset;
        _capacity = capacity;
        _pos = offset;
    }

    /// <summary>Bytes committed so far (excluding pending bits).</summary>
    public int BytesWritten => _pos - _start;

    /// <summary>Pending bits not yet flushed.</summary>
    public int PendingBits { get; private set; }

    /// <summary>Adds <paramref name="nbBits"/> low bits of <paramref name="value"/> LSB-first.</summary>
    public void AddBits(uint value, int nbBits)
    {
        if (nbBits < 0 || nbBits > 32)
        {
            throw new ZstdException("Invalid bit write size.");
        }

        if (nbBits == 32)
        {
            AddBits((ushort)value, 16);
            AddBits(value >> 16, 16);
            return;
        }

        if (nbBits == 0)
        {
            return;
        }

        var mask = nbBits == 32 ? uint.MaxValue : ((1u << nbBits) - 1);
        _bitBuf |= (value & mask) << PendingBits;
        PendingBits += nbBits;
        // Eager capacity check (including pending bits): never silently truncate.
        if ((_pos - _start) + ((PendingBits + 7) / 8) > _capacity)
        {
            throw new ZstdException("Bitstream overflow.");
        }

        while (PendingBits >= 8)
        {
            if (_pos - _start >= _capacity)
            {
                throw new ZstdException("Bitstream overflow.");
            }

            _buf[_pos++] = (byte)_bitBuf;
            _bitBuf >>= 8;
            PendingBits -= 8;
        }
    }

    /// <summary>Flushes pending bits, zero-padding the last byte. Returns total bytes.</summary>
    public int Flush()
    {
        if (PendingBits > 0)
        {
            if (_pos - _start >= _capacity)
            {
                throw new ZstdException("Bitstream overflow.");
            }

            _buf[_pos++] = (byte)_bitBuf;
            _bitBuf = 0;
            PendingBits = 0;
        }

        return BytesWritten;
    }
}

/// <summary>
/// C# port of <c>lib/common/bitstream.h</c> <c>BIT_CStream_t</c>
/// (<c>BIT_initCStream</c> / <c>BIT_addBits</c> / <c>BIT_flushBits</c> /
/// <c>BIT_closeCStream</c>). LSB-first 64-bit container; callers must
/// <see cref="FlushBits"/> before the container would overflow
/// (<c>bitPos + nbBits &lt; 64</c>), exactly like the C asserts.
/// The byte stream is LIFO: the first bits added are the last bits read
/// by <see cref="BackwardBitReader"/> — FSE/Huffman/sequence encoders must
/// emit symbols in reverse order (see <c>FSE_compress_usingCTable</c>).
/// </summary>
internal sealed class CStreamWriter
{
    private readonly byte[] _dst;
    private readonly int _start;
    private readonly int _capacity;
    private readonly int _endPtr; // start + capacity - 8 (mirrors C endPtr)
    private int _ptr;
    private ulong _container;

    /// <summary>Creates a writer over <c>dst[offset..offset+capacity)</c>.</summary>
    public CStreamWriter(byte[] dst, int offset, int capacity)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (offset + capacity > dst.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (capacity <= 8)
        {
            throw new ZstdException("Bitstream destination too small.");
        }

        _dst = dst;
        _start = offset;
        _capacity = capacity;
        _endPtr = offset + capacity - 8;
        _ptr = offset;
    }

    /// <summary>Bits currently held in the container.</summary>
    public int BitPos { get; private set; }

    /// <summary>Bytes committed (excluding the in-register tail).</summary>
    public int FlushedBytes => _ptr - _start;

    /// <summary>Adds up to 31 bits LSB-first. Caller must flush before 64-bit overflow.</summary>
    public void AddBits(ulong value, int nbBits)
    {
        if (nbBits < 0 || nbBits > 31)
        {
            throw new ZstdException("Invalid bit write size.");
        }

        if (nbBits == 0)
        {
            return;
        }

        if (BitPos + nbBits >= 64)
        {
            throw new ZstdException("Bit container overflow (FlushBits required).");
        }

        var mask = nbBits == 64 ? ulong.MaxValue : ((1UL << nbBits) - 1);
        _container |= (value & mask) << BitPos;
        BitPos += nbBits;
    }

    /// <summary>Emits full bytes little-endian (safe version: clamps on overflow).</summary>
    public void FlushBits()
    {
        var nbBytes = BitPos >> 3;
        if (_ptr <= _endPtr)
        {
            // C writes the whole 8-byte container (LEST) then advances by nbBytes;
            // the tail bytes beyond _ptr hold the remaining bits for Close().
            for (var i = 0; i < 8; i++)
            {
                _dst[_ptr + i] = (byte)(_container >> (i * 8));
            }

            _ptr += nbBytes;
            if (_ptr > _endPtr)
            {
                _ptr = _endPtr;
            }
        }

        BitPos &= 7;
        _container >>= nbBytes * 8;
    }

    /// <summary>
    /// Closes the stream: appends the 1-bit end-mark, flushes, and returns the
    /// total size. Throws <see cref="ZstdException"/> on overflow (never truncates).
    /// </summary>
    public int Close()
    {
        AddBits(1, 1); // endMark, exactly as BIT_closeCStream
        FlushBits();
        if (_ptr >= _endPtr)
        {
            throw new ZstdException("Bitstream overflow.");
        }

        if (BitPos > 0)
        {
            _dst[_ptr] = (byte)_container; // zero-padded tail byte
            return (_ptr - _start) + 1;
        }

        return _ptr - _start;
    }
}