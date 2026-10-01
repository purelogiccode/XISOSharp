using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace XISOSharp.Gui.Services;

/// <summary>
/// Opens URLs with the operating system's default browser or handler.
/// Best effort: a missing handler is an environment condition, never a bug.
/// </summary>
internal static class UrlOpener
{
    /// <summary>
    /// Opens <paramref name="url"/> with the OS default handler.
    /// </summary>
    /// <param name="url">Absolute URL to open.</param>
    /// <returns><c>true</c> when the OS accepted the launch; otherwise <c>false</c>.</returns>
    internal static bool TryOpen(string url)
    {
        try
        {
            Process? process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            process?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            // No browser/handler configured is an environment condition, not a defect.
            Log.Information(ex, "Could not open URL with the OS handler: {Url}", url);
            return false;
        }
    }
}
