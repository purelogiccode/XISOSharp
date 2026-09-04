using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// Read-only seekable <see cref="Stream"/> adapter over an <see cref="IBlockDevice"/>.
/// Lets the <c>FileStream</c>-based <c>XisoReader</c> paths (extract/list/rewrite) operate on
/// any device — plain files, memory, or <see cref="CisoBlockDevice"/> — mirroring how
/// <c>xdvdfs-cli/src/img.rs::open_image</c> hands a boxed block device to every verb.
/// Reads past the end return 0 bytes, matching <see cref="Stream"/> conventions.
/// </summary>
public sealed class BlockDeviceStream : Stream
{
    private readonly IBlockDevice _device;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    /// <summary>Wraps <paramref name="device"/> as a readable seekable stream.</summary>
    /// <param name="device">Backing block device.</param>
    /// <param name="leaveOpen">When <c>true</c>, disposing this stream leaves the device open.</param>
    public BlockDeviceStream(IBlockDevice device, bool leaveOpen = false)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _device.Length;

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
        => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;
        if (_position >= _device.Length) return 0;
        var n = _device.Read(_position, buffer);
        _position += n;
        return n;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _device.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin."),
        };
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("Block device stream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Block device stream is read-only.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_leaveOpen) _device.Dispose();
        }

        base.Dispose(disposing);
    }
}