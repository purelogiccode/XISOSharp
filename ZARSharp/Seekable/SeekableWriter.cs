namespace ZARSharp.Seekable;

using ZARSharp.Zstd;

/// <summary>
/// Seekable zstd writer: splits input into independently compressed frames
/// and appends a skippable-frame seek table (<c>Foot</c>), or returns the
/// table standalone (<c>Head</c>). Port of zeekstd's <c>RawEncoder</c> /
/// <c>Encoder</c> framing policy over streaming-style frames (unknown-size
/// parameter row, content-size-flag-0 headers), with per-frame bytes produced
/// by <see cref="ZstdCompressor"/>.
/// </summary>
/// <remarks>
/// Framing rules (mirroring the oracle):
/// <list type="bullet">
/// <item><c>Uncompressed</c>: every frame holds exactly <c>FrameSize</c>
/// uncompressed bytes except the last. The end is deferred exactly like the
/// oracle: a frame that fills up precisely is still logged once, at
/// <c>Finish</c>, never during <c>Write</c>.</item>
/// <item><c>Compressed</c>: a frame ends once its compressed size reaches the
/// threshold, evaluated after each 128 KiB input chunk (the oracle CLI's read
/// size). Mid-chunk output-buffer refills are not modeled: if the oracle ends
/// a frame strictly inside a chunk because its output buffer filled there,
/// boundaries can differ by part of a chunk. Both outputs stay valid seekable
/// files; byte-identity holds whenever no such refill fires (always the case
/// in the pinned test matrix).</item>
/// <item>Empty input still emits exactly one (empty) frame, like the oracle's
/// unconditional trailing <c>end_frame</c>.</item>
/// </list>
/// </remarks>
public sealed class SeekableWriter
{
    /// <summary>Oracle CLI read size: framing checks happen at this input granularity.</summary>
    internal const int InputChunkSize = 131072;

    private readonly int _level;
    private readonly int _frameSize;
    private readonly SeekableFrameSizePolicy _policy;
    private readonly bool _checksum;
    private readonly List<byte[]> _encodedFrames = [];
    private readonly List<(uint C, uint D)> _frameSizes = [];
    private byte[] _pending = [];
    private int _pendingLen;
    private long _consumed;
    private bool _finished;

    /// <summary>Creates a writer with the given options (CLI-matching defaults).</summary>
    public SeekableWriter(SeekableOptions? options = null)
    {
        var opt = options ?? new SeekableOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(opt.Level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opt.Level, 22);
        ArgumentOutOfRangeException.ThrowIfLessThan(opt.FrameSize, 1);
        _level = opt.Level;
        _frameSize = (int)Math.Min((long)opt.FrameSize, SeekableOptions.MaxFrameSize);
        _policy = opt.Policy;
        _checksum = opt.Checksum;
    }

    /// <summary>
    /// Frames logged so far (the open frame is excluded, like the oracle's
    /// <c>seek_table()</c> during encoding).
    /// </summary>
    public SeekTable SeekTable => SeekTable.FromSizes(_frameSizes);

    /// <summary>Appends data to the current frame, emitting full frames.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (data.IsEmpty)
        {
            return;
        }

        if (_policy == SeekableFrameSizePolicy.Uncompressed)
        {
            AppendPending(data);
            while (_pendingLen >= _frameSize)
            {
                EmitPending(_frameSize);
            }
        }
        else
        {
            // Chunk takes align to 128 KiB boundaries of the whole stream
            // (not of each Write call): the oracle reads its input in 128 KiB
            // units, so only stream-aligned chunking reproduces its framing
            // for identical logical inputs regardless of Write splitting.
            var pos = 0;
            while (pos < data.Length)
            {
                var take = (int)Math.Min(
                    InputChunkSize - _consumed % InputChunkSize, data.Length - pos);
                AppendPending(data.Slice(pos, take));
                pos += take;
                _consumed += take;
                if (MeasurePending() >= _frameSize)
                {
                    EmitPending(_pendingLen);
                }
            }
        }
    }

    /// <summary>
    /// Ends the last frame and returns frames plus the <c>Foot</c> seek table,
    /// like the oracle's <c>finish()</c>.
    /// </summary>
    public byte[] Finish()
    {
        var (data, _) = FinishCore(writeHead: false);
        return data;
    }

    /// <summary>
    /// Ends the last frame and returns the bare frames plus the standalone
    /// <c>Head</c> seek table, like the oracle's <c>--seek-table-file</c>
    /// mode (<c>end_frame</c> without an appended table, table serialized in
    /// <c>Head</c> format to a separate file).
    /// </summary>
    public (byte[] Data, byte[] SeekTable) FinishHead()
    {
        var (data, table) = FinishCore(writeHead: true);
        return (data, table!);
    }

    private int MeasurePending() =>
        ZstdCompressor.EncodeStreamingFrame(
            new ReadOnlySpan<byte>(_pending, 0, _pendingLen), _level, _checksum).Length;

    private void EmitPending(int contentLength)
    {
        var bytes = ZstdCompressor.EncodeStreamingFrame(
            new ReadOnlySpan<byte>(_pending, 0, contentLength), _level, _checksum);
        LogFrame(bytes, contentLength);
        ShiftPending(contentLength);
    }

    private void LogFrame(byte[] bytes, int contentLength)
    {
        if (_frameSizes.Count >= SeekTable.MaxFrames)
        {
            throw new ZstdException("Too many frames in seek table.");
        }

        _frameSizes.Add(((uint)bytes.Length, (uint)contentLength));
        _encodedFrames.Add(bytes);
    }

    private (byte[] Data, byte[]? Table) FinishCore(bool writeHead)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        if (_encodedFrames.Count == 0 && _pendingLen == 0)
        {
            // Empty input: the oracle still ends (and logs) one frame, and
            // ending a fresh session with no input emits the pledged-0
            // single-shot form, handled inside EncodeStreamingFrame.
            LogFrame(ZstdCompressor.EncodeStreamingFrame([], _level, _checksum), 0);
        }
        else if (_pendingLen > 0)
        {
            EmitPending(_pendingLen);
        }

        var total = 0;
        foreach (var bytes in _encodedFrames)
        {
            total = checked(total + bytes.Length);
        }

        var data = new byte[total];
        var pos = 0;
        foreach (var bytes in _encodedFrames)
        {
            Buffer.BlockCopy(bytes, 0, data, pos, bytes.Length);
            pos += bytes.Length;
        }

        if (writeHead)
        {
            return (data, SeekTable.FromSizes(_frameSizes).WriteHead());
        }

        var foot = SeekTable.FromSizes(_frameSizes).WriteFoot();
        var result = new byte[data.Length + foot.Length];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        Buffer.BlockCopy(foot, 0, result, data.Length, foot.Length);
        return (result, null);
    }

    private void AppendPending(ReadOnlySpan<byte> data)
    {
        if (_pending.Length - _pendingLen < data.Length)
        {
            var need = checked(_pendingLen + data.Length);
            var grown = Math.Max(_pending.Length * 2, need);
            grown = Math.Max(grown, 4096);
            Array.Resize(ref _pending, grown);
        }

        data.CopyTo(new Span<byte>(_pending, _pendingLen, data.Length));
        _pendingLen += data.Length;
    }

    private void ShiftPending(int count)
    {
        _pendingLen -= count;
        Buffer.BlockCopy(_pending, count, _pending, 0, _pendingLen);
    }
}
