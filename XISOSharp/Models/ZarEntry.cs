namespace XISOSharp.Models;

/// <summary>
/// One entry in a ZArchive (<c>.zar</c>) name tree.
/// </summary>
/// <param name="Path">Archive path with a leading <c>/</c> and forward slashes (e.g. <c>/sub/file.bin</c>).</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Size">Uncompressed file size in bytes (0 for directories).</param>
public sealed record ZarEntry(string Path, bool IsDirectory, ulong Size);
