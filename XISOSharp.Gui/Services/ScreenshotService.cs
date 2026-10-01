using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Serilog;
using XISOSharp.Gui.Logging;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Captures the active application window into a PNG file. The image is saved
/// inside a <c>Screenshot</c> folder next to the application executable; when
/// that location is not writable (installed under Program Files, read-only
/// media, ...) the per-user fallback
/// <c>%LOCALAPPDATA%\XISOSharp\Screenshot</c> is used instead. Capture and
/// save are best effort: failures are logged and reported as <c>null</c>, they
/// never crash the UI.
/// </summary>
internal static class ScreenshotService
{
    /// <summary>Folder name used under both the application and fallback roots.</summary>
    internal const string FolderName = "Screenshot";

    /// <summary>Application folder name used under the per-user local app data root.</summary>
    internal const string ApplicationFolderName = "XISOSharp";

    /// <summary>
    /// Gets the primary screenshot folder: <c>Screenshot</c> beside the running
    /// executable (<see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    internal static string PrimaryFolder => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>
    /// Gets the fallback screenshot folder under
    /// <c>%LOCALAPPDATA%\XISOSharp\Screenshot</c> (temp path when the local
    /// application data folder is unavailable).
    /// </summary>
    internal static string FallbackFolder
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, ApplicationFolderName, FolderName);
        }
    }

    /// <summary>
    /// Builds the screenshot file name for a capture timestamp
    /// (<c>screenshot_yyyyMMdd_HHmmss_fff.png</c>).
    /// </summary>
    /// <param name="timestamp">Local capture time.</param>
    /// <returns>Culture-invariant PNG file name.</returns>
    internal static string BuildFileName(DateTime timestamp) =>
        $"screenshot_{timestamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.png";

    /// <summary>
    /// Picks a collision-free path for <paramref name="fileName"/> inside
    /// <paramref name="folder"/> by appending <c>_1</c>, <c>_2</c>, ... before
    /// the extension; a GUID suffix is used when every numbered candidate is
    /// taken.
    /// </summary>
    /// <param name="folder">Target folder.</param>
    /// <param name="fileName">Desired file name.</param>
    /// <param name="exists">Predicate reporting whether a candidate path already exists.</param>
    /// <returns>An unused path inside <paramref name="folder"/>.</returns>
    internal static string ResolveUniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem}_{i}{extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Captures the active application window (falling back to
    /// <paramref name="fallbackWindow"/> when no window reports itself active)
    /// and saves it as a PNG.
    /// </summary>
    /// <param name="fallbackWindow">Window to capture when no active window is found.</param>
    /// <param name="timestamp">Local capture time used for the file name.</param>
    /// <returns>The saved result, or <c>null</c> when capture or saving failed.</returns>
    internal static ScreenshotResult? CaptureActive(Window fallbackWindow, DateTime timestamp)
    {
        try
        {
            Window? target = ActiveWindow(fallbackWindow);
            if (target is null)
            {
                Log.Warning("Screenshot skipped: no visible window to capture");
                return null;
            }

            return Capture(target, timestamp);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshot capture failed");
            return null;
        }
    }

    /// <summary>
    /// Renders <paramref name="window"/> and saves the image, preferring the
    /// application folder and falling back to the per-user folder when the
    /// application folder cannot be written.
    /// </summary>
    /// <param name="window">Window to render.</param>
    /// <param name="timestamp">Local capture time used for the file name.</param>
    /// <returns>The saved result, or <c>null</c> when both locations failed.</returns>
    internal static ScreenshotResult? Capture(Window window, DateTime timestamp)
    {
        try
        {
            using RenderTargetBitmap bitmap = RenderWindow(window);
            try
            {
                return SaveTo(bitmap, PrimaryFolder, timestamp, usedFallback: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ArgumentException)
            {
                // Environment condition (read-only install), not a defect.
                Log.ForContext(BugReportSink.NoBugReportProperty, true)
                    .Warning(ex, "Screenshot: application folder not writable; using the fallback folder");
                return SaveTo(bitmap, FallbackFolder, timestamp, usedFallback: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshot save failed");
            return null;
        }
    }

    /// <summary>
    /// Returns the active window among the open desktop windows, or
    /// <paramref name="fallbackWindow"/> when none reports active. Returns
    /// <c>null</c> when no window is visible.
    /// </summary>
    /// <param name="fallbackWindow">Window to use when no active window is found.</param>
    /// <returns>The window to capture, or <c>null</c>.</returns>
    internal static Window? ActiveWindow(Window fallbackWindow)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (Window window in desktop.Windows)
                {
                    if (window.IsActive)
                    {
                        return window;
                    }
                }

                if (desktop.MainWindow is { IsVisible: true } main)
                {
                    return main;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Screenshot: active-window lookup failed");
        }

        return fallbackWindow.IsVisible ? fallbackWindow : null;
    }

    private static RenderTargetBitmap RenderWindow(Window window)
    {
        double scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        int width = (int)Math.Ceiling(window.Bounds.Width * scaling);
        int height = (int)Math.Ceiling(window.Bounds.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The window has no drawable area yet.");
        }

        RenderTargetBitmap bitmap = new(
            new PixelSize(width, height),
            new Vector(96 * scaling, 96 * scaling));
        try
        {
            bitmap.Render(window);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static ScreenshotResult SaveTo(Bitmap bitmap, string folder, DateTime timestamp, bool usedFallback)
    {
        _ = Directory.CreateDirectory(folder);
        string path = ResolveUniquePath(folder, BuildFileName(timestamp), File.Exists);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Log.Information("Screenshot saved to {Path} (fallback: {Fallback})", path, usedFallback);
        return new ScreenshotResult(path, usedFallback);
    }
}
