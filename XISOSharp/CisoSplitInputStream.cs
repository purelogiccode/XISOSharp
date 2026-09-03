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
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public CisoSplitInputStream(List<FileStream> parts)
    {
        if (parts is null || parts.Count < 2)
            throw new ArgumentException("At least two split parts are required", nameof(parts));

        _parts = [.. parts];
        _starts = new long[_parts.Length];
        for (var i = 1; i < _parts.Length; i++)
            _starts[i] = _parts[i - 1].Length;
        _length = _parts[^1].Length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length) throw new ArgumentException("Offset and count exceed buffer size");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (total < buffer.Length && _position < _length)
        {
            var partIndex = _parts.Length - 1;
            for (var i = 1; i < _parts.Length; i++)
            {
                if (_starts[i] > _position)
                {
                    partIndex = i - 1;
                    break;
                }
            }

            var part = _parts[partIndex];
            var available = part.Length - _position;
            if (available <= 0) break;

            var toRead = (int)Math.Min(buffer.Length - total, available);
            part.Seek(_position, SeekOrigin.Begin);
            var n = part.Read(buffer.Slice(total, toRead));
            if (n <= 0) break;

            _position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        if (target < 0) throw new IOException("Seek before beginning of split input");
        _position = target;
        return _position;
    }

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override long Length => _length;

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override void Flush()
    {
    }

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
