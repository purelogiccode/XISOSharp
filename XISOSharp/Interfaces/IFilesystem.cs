namespace XISOSharp.Interfaces;

/// <summary>
/// Abstraction over a write-only destination filesystem for extraction
/// (xdvdfs #166, TODO #7). Lets <c>XisoReader.UnpackImage</c>
/// land files anywhere files can be opened for writing: the local disk
/// (<c>LocalFilesystem</c>), process memory (<c>MemoryFilesystem</c>), or any
/// custom store (zip archives, network uploads, GUI preview panes).
/// Paths are destination-root-relative, forward-slash separated, and
/// case-insensitive — the same convention as the reader's internal paths
/// (<c>"default.xbe"</c>, <c>"/sub/nested/file.bin"</c> and
/// <c>"sub/nested/file.bin"</c> denote the same file). The empty string or
/// <c>"/"</c> denotes the destination root itself.
/// Implementations need not be thread-safe; unpacking writes sequentially.
/// </summary>
public interface IFilesystem
{
    /// <summary>
    /// Creates or truncates the file at <paramref name="path"/> for writing
    /// (<c>FileMode.Create</c> semantics). The stream is closed by the caller
    /// when the file is complete; its final length must equal the file size
    /// reported by the image or the unpack fails with a truncation error.
    /// </summary>
    /// <param name="path">Destination-root-relative path of the file.</param>
    /// <returns>A writable, seekable stream for the file's contents.</returns>
    Stream CreateFile(string path);

    /// <summary>
    /// Creates all directories and subdirectories in <paramref name="path"/>
    /// (<c>Directory.CreateDirectory</c> semantics). Creating the root is a
    /// no-op for implementations without one.
    /// </summary>
    /// <param name="path">Destination-root-relative directory path.</param>
    void CreateDirectory(string path);

    /// <summary>
    /// Returns <c>true</c> when a file (not a directory) exists at
    /// <paramref name="path"/>. Used by <see cref="XISOSharp.UnpackOptions.SkipExisting"/>
    /// resume logic. Unresolvable paths return <c>false</c>.
    /// </summary>
    /// <param name="path">Destination-root-relative path to probe.</param>
    bool FileExists(string path);

    /// <summary>
    /// Returns the byte length of the file at <paramref name="path"/>, or
    /// <c>-1</c> when the length cannot be resolved (missing file, unreadable
    /// store). Unresolvable destinations are never skipped and always report
    /// as truncated after a failed write.
    /// </summary>
    /// <param name="path">Destination-root-relative path to measure.</param>
    long FileLength(string path);
}