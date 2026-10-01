namespace XISOSharp.Cli.Models;

/// <summary>
/// Cached result of the latest GitHub release probe, persisted to the
/// per-user cache file so offline runs do not re-probe before the check
/// interval elapses.
/// </summary>
/// <param name="CheckedUtc">UTC time of the probe that produced this entry.</param>
/// <param name="Tag">Release tag from <c>tag_name</c> (for example <c>1.0.1</c>).</param>
/// <param name="Url">Release page URL from <c>html_url</c>.</param>
/// <param name="AssetName">Expected RID bundle file name, or <c>null</c> when the RID is unmapped.</param>
/// <param name="AssetUrl">Matching bundle download URL, or <c>null</c> when absent or unmapped.</param>
internal sealed record ReleaseInfo(
    DateTime CheckedUtc,
    string Tag,
    string Url,
    string? AssetName,
    string? AssetUrl);
