namespace XISOSharp;

/// <summary>
/// Read-side of split CSO input (<c>ciso::split::SplitFileReader</c>): a seekable read-only
/// stream over the numbered part files (<c>&lt;base&gt;.&lt;n&gt;.cso</c>). Reads use global
/// positions; each part holds its data at the global position inside its own (sparse) file.
/// </summary>
/// <remarks>
/// Part boundaries follow the Rust writer: part <c>k</c> covers the global range
/// <c>[previous part's length, own length)</c>. A write that starts before the split point is
/// written whole, so a part's file can overshoot <c>k·splitPoint</c> by up to one write — the
/// next part then starts exactly at that overshoot end, which equals the previous part's file
/// length.
/// </remarks>
internal sealed class CisoSplitInputStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private bool _disposed;

    /// <summary>
    /// Creates a read-only stream over the numbered split part files.
    /// Part start offsets are derived from the previous part's file length and
    /// <see cref="Length"/> is the last part's length (the global image size).
    /// </summary>
    /// <param name="parts">Open part streams in order; at least two are required.</param>
    /// <exception cref="ArgumentException">Thrown when fewer than two parts are supplied.</exception>
    public CisoSplitInputStream(List<FileStream> parts)
    {
        if (parts is null || parts.Count < 2)
            throw new ArgumentException("At least two split parts are required", nameof(parts));

        _parts = [.. parts];
        _starts = new long[_parts.Length];
        for (var i = 1; i < _parts.Length; i++)
            _starts[i] = _parts[i - 1].Length;
        Length = _parts[^1].Length;
    }

    /// <summary>Reads bytes at the current global <see cref="Position"/> across part boundaries.</summary>
    /// <param name="buffer">Destination array.</param>
    /// <param name="offset">Offset in <paramref name="buffer"/> to write at.</param>
    /// <param name="count">Maximum number of bytes to read.</param>
    /// <returns>Number of bytes read, or 0 at end of stream.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length) throw new ArgumentException("Offset and count exceed buffer size");
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Reads bytes at the current global <see cref="Position"/> across part boundaries.</summary>
    /// <param name="buffer">Destination span.</param>
    /// <returns>Number of bytes read, or 0 at end of stream.</returns>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (total < buffer.Length && Position < Length)
        {
            var partIndex = _parts.Length - 1;
            for (var i = 1; i < _parts.Length; i++)
            {
                if (_starts[i] > Position)
                {
                    partIndex = i - 1;
                    break;
                }
            }

            var part = _parts[partIndex];
            var available = part.Length - Position;
            if (available <= 0) break;

            var toRead = (int)Math.Min(buffer.Length - total, available);
            part.Seek(Position, SeekOrigin.Begin);
            var n = part.Read(buffer.Slice(total, toRead));
            if (n <= 0) break;

            Position += n;
            total += n;
        }

        return total;
    }

    /// <summary>Seeks to a global position within the composite split image.</summary>
    /// <param name="offset">Offset relative to <paramref name="origin"/>.</param>
    /// <param name="origin">Reference point: begin, current, or end.</param>
    /// <returns>The new global position.</returns>
    /// <exception cref="IOException">Thrown when seeking before the beginning of the stream.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset
        };
        if (target < 0) throw new IOException("Seek before beginning of split input");
        Position = target;
        return Position;
    }

    /// <summary>Gets or sets the global read position across all parts.</summary>
    public override long Position { get; set; }

    /// <summary>Gets the total length of the composite split image in bytes.</summary>
    public override long Length { get; }

    /// <summary>Not supported; the split input stream is read-only.</summary>
    /// <param name="value">Unused.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>Not supported; the split input stream is read-only.</summary>
    /// <param name="buffer">Unused.</param>
    /// <param name="offset">Unused.</param>
    /// <param name="count">Unused.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>Gets a value indicating whether reading is supported (always <c>true</c>).</summary>
    public override bool CanRead => true;

    /// <summary>Gets a value indicating whether seeking is supported (always <c>true</c>).</summary>
    public override bool CanSeek => true;

    /// <summary>Gets a value indicating whether writing is supported (always <c>false</c>).</summary>
    public override bool CanWrite => false;

    /// <summary>No-op; read state is never buffered.</summary>
    public override void Flush()
    {
    }

    /// <summary>Disposes the underlying part streams.</summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            foreach (var part in _parts) part.Dispose();
        }

        base.Dispose(disposing);
    }
}