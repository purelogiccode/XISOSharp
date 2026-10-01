namespace XISOSharp.Gui.Services;

/// <summary>
/// Outcome of a window screenshot capture: where the image was written and
/// whether the application-folder location was unavailable, forcing the
/// per-user fallback folder.
/// </summary>
/// <param name="FilePath">Absolute path of the saved PNG image.</param>
/// <param name="UsedFallbackFolder">
/// <c>true</c> when the image was saved under the per-user fallback folder
/// because the application folder could not be written.
/// </param>
internal sealed record ScreenshotResult(string FilePath, bool UsedFallbackFolder);
