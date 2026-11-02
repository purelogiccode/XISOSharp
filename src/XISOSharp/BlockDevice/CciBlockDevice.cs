using CCISharp;
using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// Block device that presents a CCI ("CCIM", Cerbios Compressed Image) file as
/// an uncompressed block device. Accepts a single <c>.cci</c> container or a
/// split <c>game.1.cci</c> … <c>game.9.cci</c> set; sector decompression is
/// delegated to <see cref="CciFile"/>.
/// </summary>
/// <remarks>
/// The underlying <see cref="CciFile"/> is opened by this device and disposed
/// with it. Reads are forwarded to <see cref="CciFile.Read"/>, which is safe for
/// concurrent callers.
/// </remarks>
public sealed class CciBlockDevice : IBlockDevice
{
    private readonly CciFile _file;
    private bool _disposed;

    /// <summary>Opens a CCI image (single part or split set) as a block device.</summary>
    /// <param name="cciPath">Path to a <c>.cci</c> file or the first part of a split set.</param>
    /// <exception cref="FileNotFoundException">The image or one of its parts was not found.</exception>
    /// <exception cref="UnauthorizedAccessException">A part file could not be opened.</exception>
    /// <exception cref="InvalidDataException">The CCI header, index or size is invalid.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public CciBlockDevice(string cciPath) : this(cciPath, FileShare.Read)
    {
    }

    /// <summary>Opens a CCI image with a caller-chosen share mode for the part files.</summary>
    /// <param name="cciPath">Path to a <c>.cci</c> file or the first part of a split set.</param>
    /// <param name="share">File sharing mode passed to every part file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cciPath"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cciPath"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The image or one of its parts was not found.</exception>
    /// <exception cref="UnauthorizedAccessException">A part file could not be opened.</exception>
    /// <exception cref="InvalidDataException">The CCI header, index or size is invalid.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public CciBlockDevice(string cciPath, FileShare share)
    {
        ArgumentException.ThrowIfNullOrEmpty(cciPath);

        CciError error = CciFile.Open(
            cciPath,
            new CciReaderOptions { Share = share },
            out CciFile? file);

        if (error != CciError.None || file is null)
        {
            throw ToException(error, cciPath);
        }

        _file = file;
    }

    /// <summary>Wraps an already-open <see cref="CciFile"/>.</summary>
    /// <param name="file">CCI image; ownership transfers to this device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <c>null</c>.</exception>
    public CciBlockDevice(CciFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    /// <summary>The wrapped CCI image (for metadata such as part count and compression ratio).</summary>
    public CciFile Image => _file;

    /// <summary>Logical length of the decompressed image in bytes.</summary>
    public long Length => _file.Length;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.IsEmpty) return 0;

        long length = _file.Length;
        if (offset >= length) return 0;

        int toRead = (int)Math.Min(buffer.Length, length - offset);
        CciError error = _file.Read((ulong)offset, buffer[..toRead]);
        if (error == CciError.Io)
        {
            throw new IOException($"CCI read failed at offset {offset}: {error.ToMessage()}");
        }

        if (error != CciError.None)
        {
            throw new InvalidDataException($"CCI read failed at offset {offset}: {error.ToMessage()}");
        }

        return toRead;
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("CCI images are read-only through this block device.");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }

    private static Exception ToException(CciError error, string path) => error switch
    {
        CciError.NotFound => new FileNotFoundException($"CCI image not found: {path}", path),
        CciError.AccessDenied => new UnauthorizedAccessException($"Access denied opening CCI image: {path}"),
        CciError.InvalidHeader or
        CciError.UnsupportedHeaderSize or
        CciError.UnsupportedBlockSize or
        CciError.UnsupportedVersion or
        CciError.UnsupportedIndexAlignment or
        CciError.InvalidSize or
        CciError.InvalidIndex or
        CciError.CorruptSector or
        CciError.Truncated or
        CciError.Lz4 => new InvalidDataException($"Invalid CCI image '{path}': {error.ToMessage()}"),
        _ => new IOException($"CCI I/O error for '{path}': {error.ToMessage()}"),
    };
}
