namespace XISOSharp.Models;

/// <summary>
/// A structured progress event emitted during <see cref="XisoWriter.CreateXiso"/>
/// operations. Delivered through <see cref="IProgress{T}"/> so consumers can drive
/// progress bars, tree views, and logging from a single channel.
/// </summary>
/// <param name="Type">The kind of event; determines which payload fields are populated.</param>
/// <param name="Count">Total count for <see cref="ProgressInfoType.FileCount"/> and <see cref="ProgressInfoType.DirCount"/>; 0 otherwise.</param>
/// <param name="Path">Internal path with forward slashes for <see cref="ProgressInfoType.DirAdded"/> (e.g. <c>"/"</c>, <c>"/subdir"</c>) and <see cref="ProgressInfoType.FileAdded"/> (e.g. <c>"/subdir/file.bin"</c>); <c>null</c> otherwise.</param>
/// <param name="Sector">Start sector (partition-relative) for <see cref="ProgressInfoType.DirAdded"/> and <see cref="ProgressInfoType.FileAdded"/>; 0 otherwise.</param>
/// <param name="Size">Byte size written for <see cref="ProgressInfoType.FileAdded"/>; 0 otherwise.</param>
public readonly record struct ProgressInfo(
    ProgressInfoType Type,
    long Count = 0,
    string? Path = null,
    long Sector = 0,
    long Size = 0);
