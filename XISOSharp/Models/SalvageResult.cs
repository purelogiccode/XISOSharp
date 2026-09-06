namespace XISOSharp.Models;

/// <summary>
/// Outcome of a salvage rebuild (<see cref="XISOSharp.XisoSalvager"/>).
/// </summary>
/// <param name="Copied">
/// Image-internal paths carried into the rebuilt image (<c>/</c>-separated;
/// directories carry a trailing <c>/</c>). Names that needed sanitizing
/// (path separators → <c>_</c>) appear here under their staged name.
/// </param>
/// <param name="Skipped">
/// Human-readable lines for every entry left behind and why (truncated data,
/// tripped structural gates, orphaned right-link tails, separator-collision
/// or host-unusable names). Dropped means reported, never hidden.
/// </param>
/// <param name="OutputPath">Path of the rebuilt plain <c>.iso</c> image.</param>
/// <param name="OutputIssues">
/// Re-audit issues of the rebuilt image
/// (<see cref="XISOSharp.XisoReader.AuditXiso(string)"/>): empty when the
/// salvage output passes. A salvage that still fails is reported, not hidden.
/// </param>
public record SalvageResult(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Skipped,
    string OutputPath,
    IReadOnlyList<string> OutputIssues)
{
    /// <summary>True when the rebuilt image passes the audit.</summary>
    public bool Success => OutputIssues.Count == 0;
}
