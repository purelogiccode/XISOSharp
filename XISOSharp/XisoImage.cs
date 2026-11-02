using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Options for <see cref="XisoImage.Open(string, XisoImageOptions?)"/>.
/// </summary>
public sealed record XisoImageOptions
{
    /// <summary>
    /// Share mode used for the image file(s): the plain ISO, a CISO/CCI
    /// container, or every part of a split set. Default
    /// <see cref="FileShare.Read"/>; use <see cref="FileShare.ReadWrite"/> to
    /// coexist with sync clients or antivirus scanners that hold the file open.
    /// </summary>
    public FileShare Share { get; init; } = FileShare.Read;
}

/// <summary>
/// A disposable, open-once facade over an Xbox image: resolves the format
/// (plain ISO, split ISO, CISO, CCI), probes the volume eagerly, and exposes
/// navigation, bounded reads, extraction, validation, hashing and title
/// metadata without re-opening the file for every call.
/// </summary>
/// <remarks>
/// The facade keeps one decompressed view open for its lifetime and serializes
/// metadata/read operations internally, so one instance is safe for concurrent
/// callers. Create one instance per image; do not share the instance across
/// images. For ZAR archives use <see cref="XisoZarchive"/> — an archive is a
/// name tree, not a sector image.
/// </remarks>
public sealed class XisoImage : IDisposable, IAsyncDisposable
{
    private readonly XisoExplorer _explorer;
    private readonly XisoImageSourceInfo _source;
    private bool _disposed;

    private XisoImage(string path, XisoImageSourceInfo source, XisoExplorer explorer)
    {
        Path = path;
        _source = source;
        _explorer = explorer;
    }

    /// <summary>Gets the image path as supplied to <see cref="Open"/>.</summary>
    public string Path { get; }

    /// <summary>Gets the detected source metadata (format, length and parts).</summary>
    public XisoImageSourceInfo Source
    {
        get
        {
            ThrowIfDisposed();
            return _source;
        }
    }

    /// <summary>Gets the probed volume descriptor.</summary>
    public VolumeInfo Volume
    {
        get
        {
            ThrowIfDisposed();
            return _explorer.Volume;
        }
    }

    /// <summary>Gets the logical image length in bytes (decompressed).</summary>
    public long Length => Source.Length;

    /// <summary>
    /// Gets whether the image carries the extract-xiso optimized tag at byte
    /// offset 31337 (shifted for prepended Redump partitions).
    /// </summary>
    public bool IsOptimized
    {
        get
        {
            ThrowIfDisposed();
            return XisoReader.IsOptimizedImage(Path);
        }
    }

    /// <summary>
    /// Opens an image and probes its volume descriptor. The image remains open
    /// until <see cref="Dispose"/>.
    /// </summary>
    /// <param name="path">Plain ISO, split ISO, CISO or CCI path.</param>
    /// <param name="options">Optional share mode.</param>
    /// <returns>An open <see cref="XisoImage"/>.</returns>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="NotSupportedException">The path is a ZAR archive.</exception>
    /// <exception cref="XisoFormatException">The image is not a valid XISO.</exception>
    public static XisoImage Open(string path, XisoImageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        XisoImageOptions opts = options ?? new XisoImageOptions();

        if (!XisoImageSource.TryDetect(path, opts.Share, out XisoImageSourceInfo? source) || source is null)
        {
            throw new XisoFormatException($"Not a valid Xbox image: {path}");
        }

        if (source.Format == XisoImageFormat.Zar)
        {
            throw new NotSupportedException(
                "ZArchive (.zar) files are not sector images; use XisoZarchive.ListZar/ExtractZar/ConvertZarToIso.");
        }

        XisoExplorer explorer = new(path, new XisoExplorerOptions { KeepOpen = true, Share = opts.Share });
        return new XisoImage(path, source, explorer);
    }

    /// <summary>Asynchronous wrapper over <see cref="Open"/>.</summary>
    /// <param name="path">Image path.</param>
    /// <param name="options">Optional share mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open <see cref="XisoImage"/>.</returns>
    public static Task<XisoImage> OpenAsync(string path, XisoImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Open(path, options), cancellationToken);
    }

    /// <summary>Lists the direct children of a directory (on-disc order).</summary>
    /// <param name="internalPath">Image-internal directory path (<c>/</c> for root).</param>
    /// <returns>Child nodes.</returns>
    public IReadOnlyList<ExplorerNode> List(string internalPath = "/")
    {
        ThrowIfDisposed();
        return _explorer.ListChildren(internalPath);
    }

    /// <summary>
    /// Lists every descendant of a directory recursively, sorted by
    /// case-insensitive path for deterministic output.
    /// </summary>
    /// <param name="internalPath">Image-internal directory path (<c>/</c> for root).</param>
    /// <returns>Every node below <paramref name="internalPath"/>, directories included.</returns>
    public IReadOnlyList<ExplorerNode> ListFlat(string internalPath = "/")
    {
        ThrowIfDisposed();
        List<ExplorerNode> result = [];
        Stack<string> pending = new();
        pending.Push(XisoExplorer.Normalize(internalPath));
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (ExplorerNode child in _explorer.ListChildren(directory))
            {
                result.Add(child);
                if (child.IsDirectory)
                {
                    pending.Push(child.FullPath);
                }
            }
        }

        result.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        return result;
    }

    /// <summary>
    /// Returns the node for an exact path, or <c>null</c> when it does not exist.
    /// </summary>
    /// <param name="internalPath">Image-internal path.</param>
    public ExplorerNode? GetNode(string internalPath)
    {
        ThrowIfDisposed();
        return _explorer.GetNode(internalPath);
    }

    /// <summary>
    /// Opens a read-only, seekable stream bounded to a file's data extent.
    /// </summary>
    /// <param name="internalPath">File path within the image.</param>
    /// <returns>A bounded stream; disposing it does not close the image.</returns>
    public Stream OpenReadStream(string internalPath)
    {
        ThrowIfDisposed();
        return _explorer.OpenReadStream(internalPath);
    }

    /// <summary>Reads an entire file into memory.</summary>
    /// <param name="internalPath">File path within the image.</param>
    /// <returns>The file bytes.</returns>
    /// <exception cref="InvalidDataException">The path is missing or names a directory.</exception>
    public byte[] ReadFile(string internalPath)
    {
        ThrowIfDisposed();
        ExplorerNode node = _explorer.GetNode(internalPath)
                            ?? throw new InvalidDataException($"Path not found: {internalPath}");
        if (node.IsDirectory)
        {
            throw new InvalidDataException($"Cannot read a directory: {internalPath}");
        }

        if (node.Size > int.MaxValue)
        {
            throw new IOException($"File '{internalPath}' is too large to read into a byte array ({node.Size} bytes).");
        }

        byte[] data = new byte[node.Size];
        using Stream stream = OpenReadStream(node.FullPath);
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>Reads a bounded range of a file into a caller buffer.</summary>
    /// <param name="internalPath">File path within the image.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="fileOffset">Byte offset within the file.</param>
    /// <returns>Bytes actually read.</returns>
    public int ReadFile(string internalPath, Span<byte> buffer, long fileOffset)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        ExplorerNode node = _explorer.GetNode(internalPath)
                            ?? throw new InvalidDataException($"Path not found: {internalPath}");
        if (node.IsDirectory)
        {
            throw new InvalidDataException($"Cannot read a directory: {internalPath}");
        }

        if (buffer.IsEmpty || fileOffset >= node.Size)
        {
            return 0;
        }

        using Stream stream = OpenReadStream(node.FullPath);
        stream.Seek(fileOffset, SeekOrigin.Begin);
        int toRead = (int)Math.Min(buffer.Length, node.Size - fileOffset);
        int total = 0;
        while (total < toRead)
        {
            int read = stream.Read(buffer[total..toRead]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Copies a file or directory out of the image.</summary>
    /// <param name="internalPath">Source path within the image.</param>
    /// <param name="destinationPath">Local destination path.</param>
    /// <param name="options">Optional resume/failure options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional progress channel.</param>
    public void CopyOut(string internalPath, string destinationPath, UnpackOptions? options = null,
        CancellationToken cancellationToken = default, IProgress<ProgressInfo>? progress = null)
    {
        ThrowIfDisposed();
        _explorer.CopyOut(internalPath, destinationPath, options, cancellationToken, progress);
    }

    /// <summary>
    /// Extracts the entire image to a directory. The operation re-opens the
    /// image internally so it can run without holding the explorer's lock.
    /// </summary>
    /// <param name="outputDirectory">Destination directory.</param>
    /// <param name="options">Optional resume/failure options.</param>
    /// <param name="progress">Optional progress channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>0 on success.</returns>
    public int Extract(string outputDirectory, UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        return XisoReader.UnpackImage(Path, outputDirectory, cancellationToken, null, options, progress);
    }

    /// <summary>Asynchronous wrapper over <see cref="Extract"/>.</summary>
    public Task<int> ExtractAsync(string outputDirectory, UnpackOptions? options = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Extract(outputDirectory, options, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Performs a deep integrity audit. The optimized tag is reported but not
    /// required, so raw/unconverted dumps pass.
    /// </summary>
    public AuditResult Validate()
    {
        ThrowIfDisposed();
        return XisoReader.AuditXiso(Path, requireOptimizedTag: false);
    }

    /// <summary>
    /// Resolves the root <c>default.xbe</c>, <c>default.xex</c> or
    /// <c>default_mp.xbe</c> and returns title metadata, or <c>null</c> when no
    /// supported root executable is present.
    /// </summary>
    public TitleInfo? GetTitleInfo()
    {
        ThrowIfDisposed();
        return XisoReader.GetTitleInfo(Path);
    }

    /// <summary>Parses an XBE file anywhere in the image.</summary>
    /// <param name="internalPath">Path of the <c>.xbe</c> file.</param>
    public XbeInfo? GetXbeInfo(string internalPath)
    {
        ThrowIfDisposed();
        return _explorer.GetXbeInfo(internalPath);
    }

    /// <summary>Parses an XEX2 file anywhere in the image.</summary>
    /// <param name="internalPath">Path of the <c>.xex</c> file.</param>
    public XexInfo? GetXexInfo(string internalPath)
    {
        ThrowIfDisposed();
        return _explorer.GetXexInfo(internalPath);
    }

    /// <summary>
    /// Hashes a single file and returns the digest as uppercase hex, or
    /// <c>null</c> when the path does not exist.
    /// </summary>
    /// <param name="internalPath">File path within the image.</param>
    /// <param name="algorithm">Hash algorithm (for example MD5, SHA1 or SHA256).</param>
    public string? ComputeHashHex(string internalPath, System.Security.Cryptography.HashAlgorithmName algorithm)
    {
        ThrowIfDisposed();
        return _explorer.ComputeHashHex(internalPath, algorithm);
    }

    /// <summary>
    /// Computes the full logical-image hashes (CRC32, MD5, SHA-1, SHA-256,
    /// SHA3-256) over the decompressed byte stream, so an ISO, CISO, CCI or
    /// split ISO of the same image produces identical values.
    /// </summary>
    public XisoImageHashResult ComputeImageHash(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return XisoImageHash.Compute(Path, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _explorer.Dispose();
    }

    /// <summary>Asynchronous dispose (the underlying handle close is synchronous).</summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// Shared conversion from <see cref="XbeInfo"/> to <see cref="TitleInfo"/> so
/// the explorer facade and the static reader API agree on region formatting.
/// </summary>
internal static class TitleInfoFactory
{
    internal static TitleInfo Create(string xbePath, XbeInfo info) => new(
        xbePath,
        info.TitleName,
        info.TitleId,
        $"0x{info.TitleId:X8}",
        info.GameRegion,
        FormatRegion(info.GameRegion),
        info.DiskNumber,
        info.Version,
        info);

    internal static TitleInfo CreateXex(string xexPath, XexTitleInfo title) => new(
        xexPath,
        title.TitleName,
        title.Info.TitleId,
        title.TitleIdHex,
        title.Info.Region,
        FormatXexRegion(title.Info.Region),
        title.Info.DiscNumber,
        title.Info.Version,
        null)
    {
        Kind = TitleKind.Xex,
        Xex = title,
    };

    internal static string FormatRegion(uint region) => region switch
    {
        0 => "None",
        1 => "North America",
        2 => "Japan",
        3 => "North America, Japan",
        4 => "Rest of World",
        5 => "North America, Rest of World",
        6 => "Japan, Rest of World",
        7 => "Worldwide",
        _ => $"0x{region:X8}",
    };

    internal static string FormatXexRegion(uint region) => region switch
    {
        0 => "None",
        0xFFFFFFFF => "All",
        _ when (region & 0x000000FF) != 0 => "NTSC-U",
        _ when (region & 0x0000FF00) != 0 => "NTSC-J",
        _ when (region & 0x00FF0000) != 0 => "PAL",
        _ when (region & 0xFF000000) != 0 => "Other",
        _ => $"0x{region:X8}",
    };
}
