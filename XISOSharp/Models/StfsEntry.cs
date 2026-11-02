namespace XISOSharp.Models;

/// <summary>
/// One file or directory in a single-file Xbox 360 STFS package (XBLA or
/// XBLIG). Paths are relative to the package root and use <c>/</c> separators.
/// </summary>
/// <param name="Path">Relative path inside the package (<c>data\file.bin</c> is reported as <c>data/file.bin</c>).</param>
/// <param name="Name">Leaf name (a single path component).</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Size">File size in bytes; <c>0</c> for directories.</param>
/// <param name="StartBlock">First STFS data block (logical block number, hash tables excluded).</param>
/// <param name="BlockCount">Number of data blocks allocated to the file.</param>
/// <param name="IsContiguous">Whether the allocated blocks are consecutive.</param>
/// <param name="Created">Creation time decoded from the FAT timestamp, or <c>null</c> when absent/invalid.</param>
/// <param name="Modified">Last-write time decoded from the FAT timestamp, or <c>null</c> when absent/invalid.</param>
public sealed record StfsEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    uint StartBlock,
    uint BlockCount,
    bool IsContiguous,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);
