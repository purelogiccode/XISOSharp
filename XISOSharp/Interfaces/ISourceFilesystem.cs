namespace XISOSharp.Interfaces;

/// <summary>
/// One direct child of a directory in an <see cref="ISourceFilesystem"/>.
/// </summary>
/// <param name="Name">Entry name without separators.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Length">File length in bytes; <c>0</c> for directories.</param>
public readonly record struct SourceEntry(string Name, bool IsDirectory, long Length);

/// <summary>
/// Read-side counterpart to <see cref="IFilesystem"/>: a source store that
/// <see cref="XISOSharp.XisoWriter"/> can pack without host paths (in-memory
/// trees, archives, network stores or GUI-provided content). Paths are
/// root-relative, forward-slash separated and case-insensitive; the empty
/// string denotes the source root.
/// </summary>
/// <remarks>
/// Implementations need not be thread-safe; the writer enumerates and reads
/// sequentially. Entries may be returned in any order - the writer sorts them
/// ordinally so identical trees always produce byte-identical images.
/// </remarks>
public interface ISourceFilesystem
{
    /// <summary>
    /// Returns <c>true</c> when a directory exists at <paramref name="path"/>.
    /// The empty string always denotes a valid root.
    /// </summary>
    /// <param name="path">Root-relative directory path.</param>
    bool DirectoryExists(string path);

    /// <summary>
    /// Enumerates the direct children of <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Root-relative directory path (empty for the root).</param>
    /// <returns>Direct file and directory entries.</returns>
    IReadOnlyList<SourceEntry> EnumerateEntries(string path);

    /// <summary>
    /// Opens a file for reading. The caller disposes the returned stream.
    /// </summary>
    /// <param name="path">Root-relative file path.</param>
    /// <returns>A readable stream over the file contents.</returns>
    Stream OpenRead(string path);
}
