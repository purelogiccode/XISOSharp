using System.Security.Cryptography;
using XISOSharp.BlockDevice;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// A single file or directory inside an XISO image, as surfaced by
/// <see cref="XisoExplorer"/>. Paths are image-internal, <c>/</c>-separated,
/// case-insensitive (<c>"/sub/readme.txt"</c>).
/// </summary>
/// <param name="Name">Entry file name (no separators).</param>
/// <param name="FullPath">Image-internal path (<c>"/"</c> for the root).</param>
/// <param name="IsDirectory">Whether this node is a directory.</param>
/// <param name="Size">File byte size (0 for directories and empty files).</param>
/// <param name="StartSector">Partition-relative first sector of the data or table.</param>
/// <param name="Attributes">Raw attribute byte (see <see cref="Constants"/> for flags).</param>
public sealed record ExplorerNode(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    uint StartSector,
    byte Attributes);

/// <summary>
/// Options for <see cref="XisoExplorer(string, XisoExplorerOptions)"/>.
/// </summary>
public sealed record XisoExplorerOptions
{
    /// <summary>
    /// Keep one image stream open for the explorer's lifetime instead of
    /// opening/closing per operation. The instance becomes effectively
    /// <see cref="IDisposable"/>: disposing the explorer closes the held stream,
    /// and every stream returned by <see cref="XisoExplorer.OpenReadStream(string)"/>
    /// dies with it. Metadata operations on a keep-open explorer use the held
    /// stream and are serialized by an internal lock, so the instance is safe
    /// for concurrent callers (mirrors a VFS mounter's single-image handle).
    /// </summary>
    public bool KeepOpen { get; init; }

    /// <summary>
    /// Share mode for the underlying plain-ISO <see cref="FileStream"/>.
    /// Default <see cref="FileShare.Read"/> (unchanged 1.0.2 behavior). Use
    /// <see cref="FileShare.ReadWrite"/> to coexist with AV scanners or sync
    /// clients that open the image for write while it is mounted.
    /// </summary>
    public FileShare Share { get; init; } = FileShare.Read;
}

/// <summary>
/// UI-agnostic explorer over one XISO image (TODO #11, xdvdfs #120; CSO support
/// TODO #19): open/load lifecycle, directory navigation, per-node copy-out,
/// hashing, in-place file reads, and XEX parsing behind the
/// <see cref="XisoReader"/> primitives the WPF Tester's Explore tab binds to.
/// Stateless per call by default (every operation opens and closes the image),
/// so instances are safe for concurrent use from background workers. Pass
/// <see cref="XisoExplorerOptions.KeepOpen"/> to hold one stream for the
/// explorer's lifetime — the VFS-mounter mode — and dispose the explorer when
/// done; its operations are serialized by an internal lock.
/// Plain <c>.iso</c> images and CISO containers (single <c>.cso</c> or split
/// <c>.1.cso</c> part sets) are supported — every operation routes through
/// <see cref="XisoReader.OpenImageStream(string)"/> and the stream reader overloads.
/// </summary>
public sealed class XisoExplorer : IDisposable
{
    private readonly object _sync = new();
    private readonly Stream? _heldStream;
    private bool _disposed;

    /// <summary>
    /// Opens the image at <paramref name="isoPath"/> for exploration, probing
    /// the volume descriptor eagerly so a bad image fails fast.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c> or <c>.cso</c>).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="isoPath"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public XisoExplorer(string isoPath) : this(isoPath, new XisoExplorerOptions())
    {
    }

    /// <summary>
    /// Opens the image at <paramref name="isoPath"/> for exploration with
    /// explicit options. The volume descriptor is probed eagerly so a bad image
    /// fails fast. With <see cref="XisoExplorerOptions.KeepOpen"/> the image
    /// stream stays open until <see cref="Dispose"/>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c> or <c>.cso</c>).</param>
    /// <param name="options">Explorer options (keep-open, share mode).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="isoPath"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public XisoExplorer(string isoPath, XisoExplorerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(isoPath))
            throw new ArgumentException("Image path must not be empty.", nameof(isoPath));

        IsoPath = isoPath;
        Options = options;
        try
        {
            if (options.KeepOpen)
            {
                _heldStream = XisoReader.OpenImageStream(isoPath, options.Share);
                Volume = XisoReader.GetVolumeInfo(_heldStream, isoPath);
            }
            else
            {
                using Stream stream = XisoReader.OpenImageStream(isoPath, options.Share);
                Volume = XisoReader.GetVolumeInfo(stream, isoPath);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // A corrupt CISO container fails in the block-device layer before the
            // volume probe runs; surface it under the documented contract.
            _heldStream?.Dispose();
            _heldStream = null;
            throw new XisoFormatException($"Not a valid XISO: {isoPath}", ex);
        }

        if (!Volume.IsValid)
        {
            _heldStream?.Dispose();
            _heldStream = null;
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");
        }
    }

    /// <summary>Gets the explored image path, as passed to the constructor.</summary>
    public string IsoPath { get; }

    /// <summary>Gets the options the explorer was created with.</summary>
    public XisoExplorerOptions Options { get; }

    /// <summary>Gets the probed volume descriptor (validity already enforced).</summary>
    public VolumeInfo Volume { get; }

    /// <summary>
    /// Indicates whether the explorer holds the image stream open for its
    /// lifetime (<see cref="XisoExplorerOptions.KeepOpen"/>).
    /// </summary>
    public bool IsKeepOpen => _heldStream is not null;

    /// <summary>
    /// Opens a read-only, seekable stream over a file's data extent inside the
    /// image. Reads are bounded to the file's size (position at/after the end
    /// returns 0 bytes, per <see cref="Stream"/> conventions); seeking is
    /// relative to the file's first byte. Because it routes through the
    /// decompressed image view, <c>.cso</c>/split-<c>.cso</c> inputs work
    /// transparently. In the default stateless mode the returned stream owns the
    /// image handle — dispose it; on a keep-open explorer the stream stays valid
    /// until the explorer is disposed.
    /// </summary>
    /// <param name="internalPath">File path within the image.</param>
    /// <returns>A bounded stream over the file's data.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the path does not exist or names a directory.
    /// </exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public Stream OpenReadStream(string internalPath)
    {
        string path = Normalize(internalPath);
        if (string.Equals(path, "/", StringComparison.Ordinal))
            throw new InvalidDataException($"Cannot read a directory: {internalPath}");

        if (IsKeepOpen)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                EntryInfo? entry = XisoReader.GetEntryInfo(_heldStream!, IsoPath, path);
                if (entry is null)
                    throw new InvalidDataException($"Path not found: {internalPath}");
                if (entry.IsDirectory)
                    throw new InvalidDataException($"Cannot read a directory: {internalPath}");
                return OpenReadStreamCore(_heldStream!, entry, ownsParent: false);
            }
        }

        Stream stream = XisoReader.OpenImageStream(IsoPath, Options.Share);
        EntryInfo? entry2 = XisoReader.GetEntryInfo(stream, IsoPath, path);
        if (entry2 is null || entry2.IsDirectory)
        {
            stream.Dispose();
            throw new InvalidDataException(entry2 is null
                ? $"Path not found: {internalPath}"
                : $"Cannot read a directory: {internalPath}");
        }

        return OpenReadStreamCore(stream, entry2, ownsParent: true);
    }

    /// <summary>
    /// Opens a bounded read stream for a node previously returned by
    /// <see cref="GetNode"/> or <see cref="ListChildren"/>. Equivalent to
    /// <see cref="OpenReadStream(string)"/> but skips the path lookup.
    /// </summary>
    /// <param name="node">File node to open.</param>
    /// <returns>A bounded stream over the node's data.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="node"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="node"/> is a directory.</exception>
    /// <exception cref="XisoFormatException">Thrown when the explorer was disposed meanwhile.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public Stream OpenReadStream(ExplorerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDirectory)
            throw new ArgumentException($"Cannot read a directory: {node.FullPath}", nameof(node));

        if (IsKeepOpen)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return OpenReadStreamCore(_heldStream!, node, ownsParent: false);
            }
        }

        Stream stream = XisoReader.OpenImageStream(IsoPath, Options.Share);
        try
        {
            return OpenReadStreamCore(stream, node, ownsParent: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists the direct children of a directory within the image.
    /// </summary>
    /// <param name="internalPath">Directory path (<c>"/"</c> for the root).</param>
    /// <returns>Child nodes in on-disc order.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the path does not exist or names a file instead of a directory.
    /// </exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public IReadOnlyList<ExplorerNode> ListChildren(string internalPath)
    {
        string path = Normalize(internalPath);
        using Stream stream = OpenOperationStream();
        return XisoReader.ListDirectory(stream, IsoPath, path)
            .Select(e => FromEntry(e, Combine(path, e.Name)))
            .ToArray();
    }

    /// <summary>
    /// Returns the node for an exact path: the synthetic root for <c>"/"</c>,
    /// otherwise the entry metadata, or <c>null</c> when the path does not exist.
    /// </summary>
    /// <param name="internalPath">Image-internal path.</param>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public ExplorerNode? GetNode(string internalPath)
    {
        string path = Normalize(internalPath);
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            ThrowIfDisposed();
            return new ExplorerNode("/", "/", IsDirectory: true, Size: 0,
                Volume.RootDirSector, Attributes: 0);
        }

        using Stream stream = OpenOperationStream();
        EntryInfo? entry = XisoReader.GetEntryInfo(stream, IsoPath, path);
        return entry is null ? null : FromEntry(entry, path);
    }

    /// <summary>
    /// Copies a single file or directory out of the image (directories recurse).
    /// An existing destination is overwritten.
    /// </summary>
    /// <param name="internalPath">Source path within the ISO.</param>
    /// <param name="destPath">Destination path on the local filesystem.</param>
    /// <param name="options">Optional resume options (see <see cref="UnpackOptions"/>).</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <param name="progress">
    /// Optional structured progress channel (per-chunk <c>FileProgress</c> events).
    /// </param>
    /// <exception cref="InvalidDataException">Thrown when the internal path does not exist.</exception>
    /// <exception cref="ExtractFileException">
    /// Thrown naming the entry, its sector, and expected vs actual bytes on
    /// destination or data failures.
    /// </exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public void CopyOut(
        string internalPath,
        string destPath,
        UnpackOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        using Stream stream = OpenOperationStream();
        XisoReader.CopyOut(stream, IsoPath, Normalize(internalPath), destPath, options, cancellationToken,
            progress);
    }

    /// <summary>
    /// Hashes a single file within the image and returns the digest as
    /// uppercase hex without separators.
    /// </summary>
    /// <param name="internalPath">File path within the ISO.</param>
    /// <param name="algorithm"><see cref="HashAlgorithmName.MD5"/> or <see cref="HashAlgorithmName.SHA256"/>.</param>
    /// <returns>Hex digest, or <c>null</c> when the path does not exist.</returns>
    /// <exception cref="InvalidDataException">Thrown when the path names a directory.</exception>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public string? ComputeHashHex(string internalPath, HashAlgorithmName algorithm)
    {
        using Stream stream = OpenOperationStream();
        byte[]? hash = XisoReader.ComputeFileHash(stream, IsoPath, Normalize(internalPath), algorithm);
        return hash is null ? null : Convert.ToHexString(hash);
    }

    /// <summary>
    /// Parses the Xbox 360 XEX2 header of an executable inside the image.
    /// </summary>
    /// <param name="internalPath">Path of the <c>.xex</c> file within the ISO.</param>
    /// <returns>
    /// The parsed <see cref="XexInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XEX2 executable.
    /// </returns>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public XexInfo? GetXexInfo(string internalPath)
    {
        using Stream stream = OpenOperationStream();
        return XisoReader.GetXexInfo(stream, IsoPath, Normalize(internalPath));
    }

    /// <summary>
    /// Parses the original-Xbox XBEH header + certificate of an executable inside the image.
    /// </summary>
    /// <param name="internalPath">Path of the <c>.xbe</c> file within the ISO.</param>
    /// <returns>
    /// The parsed <see cref="XbeInfo"/>, or <c>null</c> when the path does not exist,
    /// points to a directory, or the file is not an XBEH executable.
    /// </returns>
    /// <exception cref="XisoFormatException">Thrown when the ISO is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when a keep-open explorer was disposed.</exception>
    public XbeInfo? GetXbeInfo(string internalPath)
    {
        using Stream stream = OpenOperationStream();
        return XisoReader.GetXbeInfo(stream, IsoPath, Normalize(internalPath));
    }

    /// <summary>
    /// Releases the image stream held by a keep-open explorer. Every read stream
    /// returned by that explorer becomes invalid. A stateless explorer
    /// (<see cref="XisoExplorerOptions.KeepOpen"/> <c>false</c>) has nothing to
    /// release; calling this is harmless.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _heldStream?.Dispose();
    }

    /// <summary>
    /// Joins a directory path and an entry name into an image-internal path.
    /// </summary>
    /// <param name="directory">Parent directory (<c>"/"</c> for the root).</param>
    /// <param name="name">Entry file name (no separators).</param>
    public static string Combine(string directory, string name)
    {
        string dir = Normalize(directory);
        return string.Equals(dir, "/", StringComparison.Ordinal) ? "/" + name : dir + "/" + name;
    }

    /// <summary>
    /// Normalizes an image-internal path: backslashes become forward slashes, a
    /// leading slash is enforced, and a trailing slash is stripped (except root).
    /// Empty input denotes the root.
    /// </summary>
    /// <param name="internalPath">Raw path (e.g. <c>"sub\\dir/"</c>).</param>
    public static string Normalize(string? internalPath)
    {
        if (string.IsNullOrEmpty(internalPath))
            return "/";

        string path = internalPath.Replace('\\', '/');
        if (!path.StartsWith('/'))
            path = "/" + path;

        while (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];

        return path;
    }

    private static ExplorerNode FromEntry(EntryInfo entry, string fullPath) =>
        new(entry.Name, fullPath, entry.IsDirectory, entry.FileSize,
            entry.StartSector, entry.Attributes);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Returns the held stream (keep-open mode, under <see cref="_sync"/>) or a
    /// fresh one-shot stream (stateless mode). A held stream is not disposed and
    /// remains locked until the caller's <c>using</c> ends, so caller operations
    /// must be <c>using</c>-scoped around them — lock release rides on dispose.
    /// </summary>
    private Stream OpenOperationStream()
    {
        if (IsKeepOpen)
        {
            Monitor.Enter(_sync);
            try
            {
                ThrowIfDisposed();
                return new LockHoldingStream(_heldStream!, _sync);
            }
            catch
            {
                Monitor.Exit(_sync);
                throw;
            }
        }

        return XisoReader.OpenImageStream(IsoPath, Options.Share);
    }

    private Stream OpenReadStreamCore(Stream parent, EntryInfo entry, bool ownsParent) =>
        OpenReadStreamCore(parent, entry.StartSector, entry.FileSize, ownsParent);

    private Stream OpenReadStreamCore(Stream parent, ExplorerNode node, bool ownsParent) =>
        OpenReadStreamCore(parent, node.StartSector, node.Size, ownsParent);

    private Stream OpenReadStreamCore(Stream parent, uint startSector, long fileSize, bool ownsParent) =>
        new BoundedSubStream(
            parent,
            ((long)startSector * Constants.SectorSize) + Volume.DiscLseek,
            fileSize,
            ownsParent,
            IsKeepOpen ? _sync : null);
}

/// <summary>
/// Wraps a live stream owned by a keep-open <see cref="XisoExplorer"/> so
/// disposing the wrapper releases the explorer's operation lock instead of
/// closing the underlying stream. All parent operations happen while the lock
/// is held, mirroring a VFS mounter's single-image-handle design.
/// </summary>
internal sealed class LockHoldingStream : Stream
{
    private readonly Stream _inner;
    private readonly object _sync;
    private bool _released;

    public LockHoldingStream(Stream inner, object sync)
    {
        _inner = inner;
        _sync = sync;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_released)
        {
            _released = true;
            Monitor.Exit(_sync);
        }

        base.Dispose(disposing);
    }
}
