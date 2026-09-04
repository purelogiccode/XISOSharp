namespace XISOSharp.Models;

/// <summary>
/// Type of a structured progress event emitted during write operations
/// (create/rewrite). See <see cref="ProgressInfo"/> for the payload semantics.
/// </summary>
public enum ProgressInfoType
{
    /// <summary>Total number of files in the image, emitted once before writing starts.</summary>
    FileCount,

    /// <summary>Total number of directories in the image, emitted once before writing starts.</summary>
    DirCount,

    /// <summary>A directory has been written. Payload: <see cref="ProgressInfo.Path"/> (internal path), <see cref="ProgressInfo.Sector"/>.</summary>
    DirAdded,

    /// <summary>A file has been written. Payload: <see cref="ProgressInfo.Path"/>, <see cref="ProgressInfo.Sector"/>, <see cref="ProgressInfo.Size"/>.</summary>
    FileAdded,

    /// <summary>All data has been written; the image is complete. Emitted once, last.</summary>
    FinishedPacking
}