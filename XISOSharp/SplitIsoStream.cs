namespace XISOSharp;

/// <summary>
/// Read-only, seekable stream over a split plain-ISO set
/// (<c>game.1.iso</c>, <c>game.2.iso</c>, …). Parts are contiguous byte
/// partitions, so global offsets map to <c>(part, local offset)</c> by
/// cumulative part length.
/// </summary>
/// <remarks>
/// Instances are not thread-safe; the reader APIs that consume this stream are
/// sequential. Every part stream is owned by this instance and closed on
/// <see cref="Dispose"/>.
/// </remarks>
internal sealed class SplitIsoStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private long _position;
    private bool _disposed;

    /// <summary>Opens every part in <paramref name="partPaths"/>.</summary>
    /// <param name="partPaths">Existing part paths in join order.</param>
    /// <param name="share">File sharing mode for the part files.</param>
    /// <exception cref="ArgumentException">No part paths were supplied.</exception>
    internal SplitIsoStream(IReadOnlyList<string> partPaths, FileShare share)
    {
        if (partPaths is null || partPaths.Count == 0)
        {
            throw new ArgumentException("At least one split ISO part is required.", nameof(partPaths));
        }

        _parts = new FileStream[partPaths.Count];
        _starts = new long[partPaths.Count];

        try
        {
            long position = 0;
            for (int i = 0; i < partPaths.Count; i++)
            {
                _parts[i] = new FileStream(
                    partPaths[i],
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = share,
                        BufferSize = 65536,
                        Options = FileOptions.RandomAccess,
                    });

                _starts[i] = position;
                position = checked(position + _parts[i].Length);
            }

            Length = position;
        }
        catch
        {
            DisposeParts();
            throw;
        }
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
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _position = value;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Offset and count exceed the buffer size.");

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || Position >= Length) return 0;

        int total = 0;
        while (total < buffer.Length && Position < Length)
        {
            int partIndex = FindPart(Position);
            FileStream part = _parts[partIndex];
            long localOffset = Position - _starts[partIndex];
            int toRead = (int)Math.Min(buffer.Length - total, part.Length - localOffset);
            if (toRead <= 0) break;

            part.Seek(localOffset, SeekOrigin.Begin);
            int read = part.Read(buffer.Slice(total, toRead));
            if (read <= 0) break;

            Position += read;
            total += read;
        }

        return total;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin."),
        };

        if (target < 0) throw new IOException("Cannot seek before the beginning of the stream.");
        Position = target;
        return Position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Read-only stream.
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("Split ISO streams are read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Split ISO streams are read-only.");

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("Split ISO streams are read-only.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            DisposeParts();
        }

        base.Dispose(disposing);
    }

    private int FindPart(long position)
    {
        int index = Array.BinarySearch(_starts, position);
        if (index >= 0) return index;
        return Math.Max(0, ~index - 1);
    }

    private void DisposeParts()
    {
        foreach (FileStream part in _parts)
        {
            part.Dispose();
        }
    }
}
