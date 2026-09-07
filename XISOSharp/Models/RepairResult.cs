namespace XISOSharp.Models;

/// <summary>
/// Outcome of an in-place repair pass (<see cref="XISOSharp.XisoRepairer"/>).
/// </summary>
/// <param name="Fixed">
/// Human-readable lines describing the class-C fixes applied (or, in dry-run
/// mode, the fixes that would be applied).
/// </param>
/// <param name="Remaining">
/// Post-repair audit issues (<see cref="XISOSharp.XisoReader.AuditXiso(string)"/>):
/// empty when the image now passes. Truncation, structural, and refused classes
/// are never fixed in place, so they appear here.
/// </param>
/// <param name="BackupPath">
/// Path of the <c>.old</c> pre-repair backup, or <c>null</c> when no backup was
/// written (dry-run, backups disabled, or nothing to fix).
/// </param>
/// <param name="DryRun">Whether this was a preview pass that changed nothing.</param>
public record RepairResult(
    IReadOnlyList<string> Fixed,
    IReadOnlyList<string> Remaining,
    string? BackupPath,
    bool DryRun)
{
    /// <summary>True when the image passes the audit after the repair pass.</summary>
    public bool Success => Remaining.Count == 0;
}