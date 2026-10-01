namespace XISOSharp;

/// <summary>
/// A single file or directory inside an XISO image, as surfaced by
/// <see cref="XISOSharp.XisoExplorer"/>. Paths are image-internal, <c>/</c>-separated,
/// case-insensitive (<c>"/sub/readme.txt"</c>).
/// </summary>
/// <param name="Name">Entry file name (no separators).</param>
/// <param name="FullPath">Image-internal path (<c>"/"</c> for the root).</param>
/// <param name="IsDirectory">Whether this node is a directory.</param>
/// <param name="Size">File byte size (0 for directories and empty files).</param>
/// <param name="StartSector">Partition-relative first sector of the data or table.</param>
/// <param name="Attributes">Raw attribute byte (see <see cref="XISOSharp.Constants"/> for flags).</param>
public sealed record ExplorerNode(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    uint StartSector,
    byte Attributes);

/// <summary>
/// Options for <see cref="XISOSharp.XisoExplorer(string, XisoExplorerOptions)"/>.
/// </summary>
public sealed record XisoExplorerOptions
{
    /// <summary>
    /// Keep one image stream open for the explorer's lifetime instead of
    /// opening/closing per operation. The instance becomes effectively
    /// <see cref="IDisposable"/>: disposing the explorer closes the held stream,
    /// and every stream returned by <see cref="XISOSharp.XisoExplorer.OpenReadStream(string)"/>
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
