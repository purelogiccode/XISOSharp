namespace XISOSharp.Gui.Services;

/// <summary>
/// Result of a successful startup update probe: the running version, the
/// newest published release tag, and the page to open for the download.
/// </summary>
/// <param name="LocalVersion">Running application version (MinVer informational string).</param>
/// <param name="LatestTag">Newest GitHub release tag (for example <c>1.0.2</c>).</param>
/// <param name="ReleaseUrl">Release page URL to open in the browser.</param>
internal sealed record UpdateCheckResult(string LocalVersion, string LatestTag, string ReleaseUrl);
