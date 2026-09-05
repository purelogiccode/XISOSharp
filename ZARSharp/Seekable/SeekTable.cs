namespace ZARSharp.Seekable;

using ZARSharp.Zstd;

/// <summary>
/// Seek table of a seekable zstd archive: the frame boundaries that let a
/// decoder jump straight to the frames holding requested data. Port of
/// <c>SeekTable</c> / <c>Parser</c> / <c>Serializer</c> in
/// <c>lib/src/seek_table.rs</c> (zeekstd 0.4.5) over
/// <c>seekable_format.md</c> v0.1.1. Entries are cumulative start offsets
/// (one extra trailing entry marks the end of the last frame), exactly like
/// the oracle's <c>Entries</c> vector.
/// </summary>
public sealed class SeekTable
{
    /// <summary>Seek-table integrity magic (<c>SEEKABLE_MAGIC_NUMBER</c>).</summary>
    public const uint SeekableMagic = 0x8F92EAB1;

    /// <summary>Skippable-frame magic of the seek-table frame.</summary>
    public const uint SkippableMagic = 0x184D2A5E;

    /// <summary>Maximum frame count (<c>SEEKABLE_MAX_FRAMES</c>).</summary>
    public const uint MaxFrames = 0x08000000;

    private const int SkippableHeaderSize = 8;
    private const int IntegritySize = 9;
    private const int EntrySize = 8;

    private readonly ulong[] _cStarts;
    private readonly ulong[] _dStarts;

    private SeekTable(ulong[] cStarts, ulong[] dStarts)
    {
        _cStarts = cStarts;
        _dStarts = dStarts;
    }

    /// <summary>Number of logged frames.</summary>
    public int FrameCount => _cStarts.Length - 1;

    /// <summary>Total compressed bytes (end of the last frame).</summary>
    public ulong TotalComp => _cStarts[_cStarts.Length - 1];

    /// <summary>Total decompressed bytes (end of the last frame).</summary>
    public ulong TotalDecomp => _dStarts[_dStarts.Length - 1];

    /// <summary>
    /// Builds a table from per-frame <c>(compressed, decompressed)</c> sizes,
    /// like repeated <c>log_frame</c> calls. Rejects counts past
    /// <see cref="MaxFrames"/>.
    /// </summary>
    internal static SeekTable FromSizes(IReadOnlyList<(uint C, uint D)> frames)
    {
        if (frames.Count > MaxFrames)
        {
            throw new ZstdException("Too many frames in seek table.");
        }

        var c = new ulong[frames.Count + 1];
        var d = new ulong[frames.Count + 1];
        for (var i = 0; i < frames.Count; i++)
        {
            c[i + 1] = c[i] + frames[i].C;
            d[i + 1] = d[i] + frames[i].D;
        }

        return new SeekTable(c, d);
    }

    /// <summary>
    /// Parses an embedded <c>Foot</c> seek table from the tail of a seekable
    /// file (<c>SeekTable::from_seekable</c>). Accepts legacy
    /// checksum-bearing tables (checksums ignored, per v0.1.1). Throws
    /// <see cref="ZstdException"/> when the magic, reserved descriptor bits,
    /// frame count, or skippable size check fails.
    /// </summary>
    /// <param name="fileBytes">Entire seekable file.</param>
    public static SeekTable ParseFoot(ReadOnlySpan<byte> fileBytes)
    {
        if (fileBytes.Length < IntegritySize)
        {
            throw new ZstdException("File too short for seek table.");
        }

        var integrity = fileBytes.Slice(fileBytes.Length - IntegritySize);
        ParseIntegrity(integrity, out var numFrames, out var sizePerFrame);
        var tableSize = (long)numFrames * sizePerFrame + SkippableHeaderSize + IntegritySize;
        if (tableSize > fileBytes.Length)
        {
            throw new ZstdException("Truncated seek table.");
        }

        var start = fileBytes.Length - (int)tableSize;
        VerifySkippableHeader(fileBytes.Slice(start, SkippableHeaderSize), tableSize);
        return ParseEntries(fileBytes.Slice(start + SkippableHeaderSize), numFrames, sizePerFrame);
    }

    /// <summary>
    /// Parses a standalone <c>Head</c> seek table
    /// (<c>SeekTable::from_reader</c>): skippable header, integrity field,
    /// then entries. Trailing bytes past the table are ignored, like the
    /// oracle which stops once every entry is parsed.
    /// </summary>
    /// <param name="tableBytes">Standalone seek-table bytes.</param>
    public static SeekTable ParseHead(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < SkippableHeaderSize + IntegritySize)
        {
            throw new ZstdException("File too short for seek table.");
        }

        if (ReadLe32(tableBytes, 0) != SkippableMagic)
        {
            throw new ZstdException("Not a seek table (bad skippable magic).");
        }

        ParseIntegrity(tableBytes.Slice(SkippableHeaderSize, IntegritySize), out var numFrames, out var sizePerFrame);
        var tableSize = (long)numFrames * sizePerFrame + SkippableHeaderSize + IntegritySize;
        if ((long)ReadLe32(tableBytes, 4) + SkippableHeaderSize != tableSize)
        {
            throw new ZstdException("Corrupt seek table (size mismatch).");
        }

        if (tableSize > tableBytes.Length)
        {
            throw new ZstdException("Truncated seek table.");
        }

        return ParseEntries(
            tableBytes.Slice(SkippableHeaderSize + IntegritySize), numFrames, sizePerFrame);
    }

    /// <summary>Serializes in <c>Foot</c> format (integrity last).</summary>
    public byte[] WriteFoot()
    {
        var buf = new byte[SkippableHeaderSize + IntegritySize + FrameCount * EntrySize];
        WriteLe32(buf, 0, SkippableMagic);
        WriteLe32(buf, 4, (uint)(FrameCount * EntrySize + IntegritySize));
        WriteEntries(buf, SkippableHeaderSize);
        WriteIntegrity(buf, SkippableHeaderSize + FrameCount * EntrySize);
        return buf;
    }

    /// <summary>Serializes in <c>Head</c> format (integrity first).</summary>
    public byte[] WriteHead()
    {
        var buf = new byte[SkippableHeaderSize + IntegritySize + FrameCount * EntrySize];
        WriteLe32(buf, 0, SkippableMagic);
        WriteLe32(buf, 4, (uint)(FrameCount * EntrySize + IntegritySize));
        WriteIntegrity(buf, SkippableHeaderSize);
        WriteEntries(buf, SkippableHeaderSize + IntegritySize);
        return buf;
    }

    /// <summary>Compressed start offset of frame <paramref name="index"/>.</summary>
    public ulong FrameStartComp(int index)
    {
        CheckIndex(index);
        return _cStarts[index];
    }

    /// <summary>Compressed end offset of frame <paramref name="index"/>.</summary>
    public ulong FrameEndComp(int index)
    {
        CheckIndex(index);
        return _cStarts[index + 1];
    }

    /// <summary>Compressed size of frame <paramref name="index"/>.</summary>
    public ulong FrameSizeComp(int index)
    {
        CheckIndex(index);
        return _cStarts[index + 1] - _cStarts[index];
    }

    /// <summary>Decompressed start offset of frame <paramref name="index"/>.</summary>
    public ulong FrameStartDecomp(int index)
    {
        CheckIndex(index);
        return _dStarts[index];
    }

    /// <summary>Decompressed end offset of frame <paramref name="index"/>.</summary>
    public ulong FrameEndDecomp(int index)
    {
        CheckIndex(index);
        return _dStarts[index + 1];
    }

    /// <summary>Decompressed size of frame <paramref name="index"/>.</summary>
    public ulong FrameSizeDecomp(int index)
    {
        CheckIndex(index);
        return _dStarts[index + 1] - _dStarts[index];
    }

    /// <summary>Largest compressed frame size (0 when empty).</summary>
    public ulong MaxFrameSizeComp()
    {
        ulong max = 0;
        for (var i = 0; i < FrameCount; i++)
        {
            max = Math.Max(max, _cStarts[i + 1] - _cStarts[i]);
        }

        return max;
    }

    /// <summary>Largest decompressed frame size (0 when empty).</summary>
    public ulong MaxFrameSizeDecomp()
    {
        ulong max = 0;
        for (var i = 0; i < FrameCount; i++)
        {
            max = Math.Max(max, _dStarts[i + 1] - _dStarts[i]);
        }

        return max;
    }

    /// <summary>
    /// Index of the frame containing compressed <paramref name="offset"/>
    /// (<c>frame_index_comp</c>). Offsets past the end clamp to the last
    /// frame, like the oracle.
    /// </summary>
    public int FrameIndexAtComp(ulong offset) => FrameIndexAt(_cStarts, offset);

    /// <summary>
    /// Index of the frame containing decompressed <paramref name="offset"/>
    /// (<c>frame_index_decomp</c>). Offsets past the end clamp to the last
    /// frame, like the oracle.
    /// </summary>
    public int FrameIndexAtDecomp(ulong offset) => FrameIndexAt(_dStarts, offset);

    private int FrameIndexAt(ulong[] starts, ulong offset)
    {
        if (FrameCount == 0)
        {
            throw new ZstdException("Seek table has no frames.");
        }

        if (offset >= starts[FrameCount])
        {
            return FrameCount - 1;
        }

        // Exact port of the oracle's binary search (floor midpoint).
        var low = 0;
        var high = FrameCount;
        while (low + 1 < high)
        {
            var mid = low + ((high - low) >> 1);
            if (starts[mid] <= offset)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private void CheckIndex(int index)
    {
        if (index < 0 || index >= FrameCount)
        {
            throw new ZstdException("Frame index too large.");
        }
    }

    private static void ParseIntegrity(ReadOnlySpan<byte> integrity, out uint numFrames, out int sizePerFrame)
    {
        if (ReadLe32(integrity, 5) != SeekableMagic)
        {
            throw new ZstdException("Not a seek table (bad seekable magic).");
        }

        if (((integrity[4] >> 2) & 0x1F) != 0)
        {
            throw new ZstdException("Corrupt seek table (reserved descriptor bits set).");
        }

        numFrames = ReadLe32(integrity, 0);
        if (numFrames > MaxFrames)
        {
            throw new ZstdException("Frame index too large.");
        }

        // Legacy checksum-bearing entries are 12 bytes; the trailing
        // checksum of each entry is skipped on parse and never written.
        sizePerFrame = (integrity[4] & 0x80) != 0 ? 12 : EntrySize;
    }

    private static void VerifySkippableHeader(ReadOnlySpan<byte> header, long tableSize)
    {
        if (ReadLe32(header, 0) != SkippableMagic)
        {
            throw new ZstdException("Not a seek table (bad skippable magic).");
        }

        if ((long)ReadLe32(header, 4) + SkippableHeaderSize != tableSize)
        {
            throw new ZstdException("Corrupt seek table (size mismatch).");
        }
    }

    private static SeekTable ParseEntries(ReadOnlySpan<byte> entries, uint numFrames, int sizePerFrame)
    {
        var c = new ulong[numFrames + 1];
        var d = new ulong[numFrames + 1];
        var pos = 0;
        for (uint i = 0; i < numFrames; i++)
        {
            c[i + 1] = c[i] + ReadLe32(entries, pos);
            d[i + 1] = d[i] + ReadLe32(entries, pos + 4);
            pos += sizePerFrame;
        }

        return new SeekTable(c, d);
    }

    private void WriteIntegrity(byte[] buf, int offset)
    {
        // Descriptor is always 0: v0.1.1 tables never carry checksums.
        WriteLe32(buf, offset, (uint)FrameCount);
        buf[offset + 4] = 0;
        WriteLe32(buf, offset + 5, SeekableMagic);
    }

    private void WriteEntries(byte[] buf, int offset)
    {
        for (var i = 0; i < FrameCount; i++)
        {
            WriteLe32(buf, offset + i * EntrySize, (uint)(_cStarts[i + 1] - _cStarts[i]));
            WriteLe32(buf, offset + i * EntrySize + 4, (uint)(_dStarts[i + 1] - _dStarts[i]));
        }
    }

    private static uint ReadLe32(ReadOnlySpan<byte> buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static void WriteLe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
