namespace ZARSharp.Pipeline;

/// <summary>
/// One entry of a pack source: a directory to create or a file to store.
/// Paths use <c>/</c> separators, relative to the archive root.
/// </summary>
public sealed class ZarPackEntry
{
    /// <summary>Archive-relative path with <c>/</c> separators.</summary>
    public required string RelativePath { get; init; }

    /// <summary>True for directories, false for files.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>File length in bytes (0 for directories).</summary>
    public long Length { get; init; }

    /// <summary>Opens the file content for reading (files only).</summary>
    public Func<Stream>? OpenRead { get; init; }
}
