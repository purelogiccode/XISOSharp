namespace XISOSharp.BlockDevice;

/// <summary>
/// <see cref="IBlockDevice"/> backed by a <see cref="FileStream"/>.
/// Mirrors <c>xdvdfs</c> <c>File</c> impl for <c>BlockDeviceRead/Write</c>.
/// </summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly bool _leaveOpen;

    /// <summary>Wraps an existing <see cref="FileStream"/>.</summary>
    /// <param name="fs">Underlying file stream (must be seekable).</param>
    /// <param name="leaveOpen">When <c>true</c>, disposing this device does not close <paramref name="fs"/>.</param>
    public FileBlockDevice(FileStream fs, bool leaveOpen = false)
    {
        BaseStream = fs ?? throw new ArgumentNullException(nameof(fs));
        if (!fs.CanSeek) throw new ArgumentException("Stream must be seekable", nameof(fs));
        _leaveOpen = leaveOpen;
    }

    /// <summary>Opens a file as a block device.</summary>
    public FileBlockDevice(string path, FileMode mode = FileMode.Open, FileAccess access = FileAccess.ReadWrite,
        FileShare share = FileShare.Read, int bufferSize = 65536)
    {
        BaseStream = new FileStream(path,
            new FileStreamOptions { Mode = mode, Access = access, Share = share, BufferSize = bufferSize });
        _leaveOpen = false;
    }

    /// <inheritdoc/>
    public long Length => BaseStream.Length;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        BaseStream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = BaseStream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        BaseStream.Seek(offset, SeekOrigin.Begin);
        BaseStream.Write(buffer);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_leaveOpen) BaseStream.Dispose();
    }

    /// <summary>Exposes the underlying stream for interop.</summary>
    public FileStream BaseStream { get; }
}