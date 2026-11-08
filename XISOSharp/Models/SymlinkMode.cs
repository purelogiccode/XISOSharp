namespace XISOSharp.Models;

/// <summary>
/// How create/pack/apply operations treat host filesystem links (symbolic
/// links, junctions and other reparse points).
/// </summary>
public enum SymlinkMode
{
    /// <summary>
    /// Default. File links pack their target's bytes (XISO has no link
    /// representation); directory reparse points are skipped and never
    /// descended, so cyclic links cannot loop the walk.
    /// </summary>
    Follow,

    /// <summary>
    /// Skip every file and directory link. Skipped links are logged and the
    /// walk continues.
    /// </summary>
    Skip,

    /// <summary>
    /// Fail the operation with the offending link path. Nothing is written.
    /// </summary>
    Reject,
}
