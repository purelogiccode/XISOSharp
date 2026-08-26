namespace XISOSharp.DataStructures;

/// <summary>
/// Context object passed through the directory-offset calculation traversal.
/// Tracks the current sector counter being assigned to directory entries.
/// </summary>
internal class OffsetCalcContext
{
    /// <summary>Current sector number being assigned by the offset calculator.</summary>
    public uint CurrentSector;

    /// <summary>Byte offset prepended to all physical write positions (skip/prepend support).</summary>
    public long PrependOffset;
}