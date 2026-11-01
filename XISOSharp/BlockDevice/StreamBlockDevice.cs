using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// <see cref="IBlockDevice"/> backed by an arbitrary seekable <see cref="Stream"/>.
/// Lets containers that expose a seekable stream (split ISO parts, future
/// archive-backed images) participate in the block-device APIs used by
/// <see cref="XisoReader"/> and <see cref="XisoChecksum"/>.
/// </summary>
/// <remarks>
/// Calls are serialized on an internal lock, so one instance can be shared by
/// concurrent readers. The wrapped stream is disposed on <see cref="Dispose"/>
/// unless <c>leaveOpen</c> was requested.
/// </remarks>
public sealed class StreamBlockDevice : IBlockDevice
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
#pragma warning disable MA0158 // object keeps net8 compat (System.Threading.Lock is net9+)
    private readonly object _sync = new();
#pragma warning restore MA0158
    private bool _disposed;

    /// <summary>Wraps a readable, seekable stream.</summary>
    /// <param name="stream">Backing stream; must support reading and seeking.</param>
    /// <param name="leaveOpen">
    /// When <c>true</c>, disposing the device leaves <paramref name="stream"/> open.
    /// </param>
    /// <exception cref="ArgumentNullException">The stream is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The stream is not readable or not seekable.</exception>
    public StreamBlockDevice(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable", nameof(stream));
        _leaveOpen = leaveOpen;
    }

    /// <summary>Logical length of the device in bytes.</summary>
    public long Length
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length;
            }
        }
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty) return 0;

            long length = _stream.Length;
            if (offset >= length) return 0;

            int toRead = (int)Math.Min(buffer.Length, length - offset);
            _stream.Seek(offset, SeekOrigin.Begin);

            int total = 0;
            while (total < toRead)
            {
                int read = _stream.Read(buffer[total..toRead]);
                if (read == 0) break;
                total += read;
            }

            return total;
        }
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_stream.CanWrite) throw new NotSupportedException("Underlying stream is not writable.");
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(buffer);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_leaveOpen) _stream.Dispose();
        }
    }
}
