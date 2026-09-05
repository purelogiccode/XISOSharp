namespace ZARSharp.Seekable;

using ZARSharp.Zstd;

/// <summary>
/// Seekable zstd reader: parses the seek table (embedded <c>Foot</c> or an
/// externally supplied table, e.g. standalone <c>Head</c>) and decompresses
/// whole files, frame windows, or arbitrary byte ranges while decoding only
/// the frames the range touches. Port of zeekstd's <c>Decoder</c> range logic
/// (<c>lib/src/decode.rs</c>) over the C# frame decoder.
/// </summary>
public sealed class SeekableReader
{
    private readonly byte[] _data;

    /// <summary>
    /// Opens a seekable file and parses its embedded <c>Foot</c> seek table
    /// (<c>Decoder::new</c>). Throws <see cref="ZstdException"/> when no valid
    /// table is present.
    /// </summary>
    public SeekableReader(byte[] data)
        : this(data, SeekTable.ParseFoot(data))
    {
    }

    /// <summary>
    /// Opens frame bytes with an externally supplied seek table: standalone
    /// <c>Head</c> tables, or tables parsed separately
    /// (<c>DecodeOptions::seek_table</c>).
    /// </summary>
    public SeekableReader(byte[] data, SeekTable table)
    {
        _data = data;
        Table = table;
        if (Table.TotalComp > (ulong)data.Length)
        {
            throw new ZstdException("Seek table points past the input.");
        }
    }

    /// <summary>Parsed seek table.</summary>
    public SeekTable Table { get; }

    /// <summary>Total decompressed size.</summary>
    public long DecompressedLength => (long)Table.TotalDecomp;

    /// <summary>Number of frames.</summary>
    public int FrameCount => Table.FrameCount;

    /// <summary>Decompresses the whole payload.</summary>
    public byte[] DecompressAll() => DecompressRange(0, DecompressedLength);

    /// <summary>
    /// Decompresses <paramref name="length"/> bytes from decompressed
    /// <paramref name="offset"/>, decoding only the frames the range touches
    /// (mid-frame starts decompress from the frame start, like the oracle's
    /// dummy decompression up to the offset).
    /// </summary>
    public byte[] DecompressRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((ulong)offset + (ulong)length > Table.TotalDecomp)
        {
            throw new ZstdException("Decompression range out of bounds.");
        }

        if (length == 0)
        {
            return [];
        }

        var first = Table.FrameIndexAtDecomp((ulong)offset);
        var last = Table.FrameIndexAtDecomp((ulong)(offset + length - 1));
        var result = new byte[length];
        var pos = 0;
        for (var f = first; f <= last; f++)
        {
            var frameBytes = DecodeFrame(f);
            var frameStart = (long)Table.FrameStartDecomp(f);
            var from = Math.Max(offset - frameStart, 0);
            var to = Math.Min(offset + length - frameStart, frameBytes.Length);
            if (to > from)
            {
                Buffer.BlockCopy(frameBytes, (int)from, result, pos, (int)(to - from));
                pos += (int)(to - from);
            }
        }

        if (pos != length)
        {
            throw new ZstdException("Frame data does not match the seek table.");
        }

        return result;
    }

    /// <summary>
    /// Decompresses frames <paramref name="first"/> through
    /// <paramref name="lastInclusive"/> concatenated
    /// (<c>set_lower_frame</c> / <c>set_upper_frame</c>).
    /// </summary>
    public byte[] DecompressFrames(int first, int lastInclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        if (lastInclusive < first || lastInclusive >= Table.FrameCount)
        {
            throw new ZstdException("Frame index too large.");
        }

        var start = (long)Table.FrameStartDecomp(first);
        var end = (long)Table.FrameEndDecomp(lastInclusive);
        return DecompressRange(start, end - start);
    }

    private byte[] DecodeFrame(int index)
    {
        var start = Table.FrameStartComp(index);
        var size = Table.FrameSizeComp(index);
        var dSize = Table.FrameSizeDecomp(index);
        if (dSize > int.MaxValue)
        {
            throw new ZstdException("Frame too large to decode.");
        }

        var slice = new ReadOnlySpan<byte>(_data, (int)start, (int)size);
        return ZstdCompressor.DecompressFrame(slice, (int)dSize);
    }
}
