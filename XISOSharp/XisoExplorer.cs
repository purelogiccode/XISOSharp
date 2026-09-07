using System.Security.Cryptography;
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
/// UI-agnostic explorer over one XISO image (TODO #11, xdvdfs #120; CSO support
/// TODO #19): open/load lifecycle, directory navigation, per-node copy-out,
/// hashing, and XEX parsing behind the <see cref="XisoReader"/> primitives the
/// WPF Tester's Explore tab binds to. Stateless per call (every operation opens
/// and closes the image), so instances are safe for concurrent use from
/// background workers.
/// Plain <c>.iso</c> images and CISO containers (single <c>.cso</c> or split
/// <c>.1.cso</c> part sets) are supported — every operation routes through
/// <see cref="XisoReader.OpenImageStream"/> and the stream reader overloads.
/// </summary>
public sealed class XisoExplorer
{
    /// <summary>
    /// Opens the image at <paramref name="isoPath"/> for exploration, probing
    /// the volume descriptor eagerly so a bad image fails fast.
    /// </summary>
    /// <param name="isoPath">Path to the XISO image (plain <c>.iso</c> or <c>.cso</c>).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="isoPath"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="XisoFormatException">Thrown when the file is not a valid XISO image.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public XisoExplorer(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
            throw new ArgumentException("Image path must not be empty.", nameof(isoPath));

        IsoPath = isoPath;
        try
        {
            using Stream stream = XisoReader.OpenImageStream(isoPath);
            Volume = XisoReader.GetVolumeInfo(stream, isoPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // A corrupt CISO container fails in the block-device layer before the
            // volume probe runs; surface it under the documented contract.
            throw new XisoFormatException($"Not a valid XISO: {isoPath}", ex);
        }

        if (!Volume.IsValid)
            throw new XisoFormatException($"Not a valid XISO: {isoPath}");
    }

    /// <summary>Gets the explored image path, as passed to the constructor.</summary>
    public string IsoPath { get; }

    /// <summary>Gets the probed volume descriptor (validity already enforced).</summary>
    public VolumeInfo Volume { get; }

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
    public IReadOnlyList<ExplorerNode> ListChildren(string internalPath)
    {
        string path = Normalize(internalPath);
        using Stream stream = XisoReader.OpenImageStream(IsoPath);
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
    public ExplorerNode? GetNode(string internalPath)
    {
        string path = Normalize(internalPath);
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return new ExplorerNode("/", "/", IsDirectory: true, Size: 0,
                Volume.RootDirSector, Attributes: 0);
        }

        using Stream stream = XisoReader.OpenImageStream(IsoPath);
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
    public void CopyOut(
        string internalPath,
        string destPath,
        UnpackOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<ProgressInfo>? progress = null)
    {
        using Stream stream = XisoReader.OpenImageStream(IsoPath);
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
    public string? ComputeHashHex(string internalPath, HashAlgorithmName algorithm)
    {
        using Stream stream = XisoReader.OpenImageStream(IsoPath);
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
    public XexInfo? GetXexInfo(string internalPath)
    {
        using Stream stream = XisoReader.OpenImageStream(IsoPath);
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
    public XbeInfo? GetXbeInfo(string internalPath)
    {
        using Stream stream = XisoReader.OpenImageStream(IsoPath);
        return XisoReader.GetXbeInfo(stream, IsoPath, Normalize(internalPath));
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
}
