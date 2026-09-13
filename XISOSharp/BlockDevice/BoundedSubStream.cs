namespace XISOSharp.BlockDevice;

/// <summary>
/// Read-only, seekable window over a region of a parent stream (a file's data
/// extent inside an image). Reads are clamped to the window: seeking at/after
/// the window end and reading returns 0 bytes, per <see cref="Stream"/>
/// conventions. A corrupt directory entry must never turn a file read into a
/// read of the next file's sectors, so the window length is authoritative even
/// when the parent stream continues.
/// </summary>
/// <remarks>
/// <para>
/// With <c>ownsParent: false</c> the parent is left open on dispose — used by
/// keep-open explorers, whose lifetime bounds every stream they hand out. With
/// <c>ownsParent: true</c> disposing the window closes the parent too, so a
/// one-shot read cannot leak the image handle.
/// </para>
/// <para>
/// The window re-seeks the parent for every read, so multiple windows may share
/// one parent as long as their operations are serialized by the caller (the
/// explorer's internal lock does this). A <see cref="BlockDeviceStream"/>-based
/// parent (CISO) is safe under the same discipline because each <c>Read</c>
/// performs its own positional block-device read.
/// </para>
/// </remarks>
internal sealed class BoundedSubStream : Stream
{
    private readonly Stream _parent;
    private readonly long _start;
    private readonly bool _ownsParent;
    private readonly object? _sync;
    private readonly Action? _onDisposed;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Wraps <c>[start, start + length)</c> of <paramref name="parent"/> as a
    /// readable stream.
    /// </summary>
    /// <param name="parent">Seekable source stream.</param>
    /// <param name="start">Absolute byte offset where the window begins.</param>
    /// <param name="length">Window length in bytes.</param>
    /// <param name="ownsParent">When <c>true</c>, disposing the window disposes the parent.</param>
    /// <param name="sync">Optional lock serializing parent I/O (keep-open explorer uses it).</param>
    /// <param name="onDisposed">Optional callback invoked once when the window is disposed.</param>
    public BoundedSubStream(
        Stream parent,
        long start,
        long length,
        bool ownsParent,
        object? sync = null,
        Action? onDisposed = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _parent = parent;
        _start = start;
        Length = length;
        _ownsParent = ownsParent;
        _sync = sync;
        _onDisposed = onDisposed;
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length { get; }

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= Length)
            return 0;

        int toRead = (int)Math.Min(buffer.Length, Length - _position);
        lock (_sync ?? this)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _parent.Seek(_start + _position, SeekOrigin.Begin);
            int read = _parent.Read(buffer[..toRead]);
            _position += read;
            return read;
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin."),
        };
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("File data stream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("File data stream is read-only.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _onDisposed?.Invoke();
            if (_ownsParent)
                _parent.Dispose();
        }

        base.Dispose(disposing);
    }
}
