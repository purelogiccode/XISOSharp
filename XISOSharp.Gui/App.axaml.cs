using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.Services;
using XISOSharp.Gui.ViewModels;
using XISOSharp.Gui.Views;

namespace XISOSharp.Gui;

/// <summary>
/// Avalonia application. Loads XAML resources and wires the main window to a fresh
/// <see cref="ViewModels.MainViewModel"/> on desktop startup.
/// </summary>
public class App : Application
{
    /// <summary>
    /// Loads the compiled Avalonia XAML resources.
    /// </summary>
    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "App.Initialize failed");
            throw;
        }
    }

    /// <summary>
    /// Creates the main window with its view-model when the desktop lifetime is ready.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                MainViewModel viewModel = new();
                MainWindow window = new()
                {
                    DataContext = viewModel,
                };
                desktop.MainWindow = window;
                _ = StartupAsync(viewModel, window);
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "App startup failed");
            throw;
        }
    }

    private static async Task StartupAsync(MainViewModel viewModel, MainWindow window)
    {
        try
        {
            // Launch ping for usage stats (fire-and-forget; test hosts and
            // XISO_DISABLE_STATS=1 are honored inside ApplicationStats).
            ApplicationStats.RecordLaunch("xisosharp-gui");
            await viewModel.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI startup initialization failed");
            try
            {
                viewModel.LogMessage($"[GUI] Startup initialization failed: {ex.Message}");
            }
            catch
            {
                // ignored
            }
        }

        await CheckForUpdatesAsync(viewModel, window).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes GitHub for a newer release once at startup and, when one exists,
    /// asks the user whether to open the release page. Best effort: failures
    /// are debug-logged and never block or fail startup.
    /// </summary>
    private static async Task CheckForUpdatesAsync(MainViewModel viewModel, MainWindow window)
    {
        try
        {
            UpdateCheckResult? update = await UpdateService.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            if (update is null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!await WaitForShownAsync(window).ConfigureAwait(true))
                {
                    return;
                }

                string tag = update.LatestTag.TrimStart('v', 'V');
                bool open = await MessageBoxWindow.ShowAsync(
                    window,
                    "Update available",
                    $"XISOSharp {tag} is available (you have {update.LocalVersion}).\n\n" +
                    "Open the GitHub release page to download the new version?",
                    "Open GitHub",
                    "Later").ConfigureAwait(true);
                if (open && !UrlOpener.TryOpen(update.ReleaseUrl))
                {
                    viewModel.LogMessage("[GUI] Could not open the release page in the default browser.");
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI update check failed");
        }
    }

    /// <summary>
    /// Completes when the window has been shown; <c>false</c> when it closed
    /// before ever opening (so the caller can skip showing a dialog).
    /// </summary>
    private static async Task<bool> WaitForShownAsync(Window window)
    {
        if (window.IsVisible)
        {
            return true;
        }

        TaskCompletionSource<bool> shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler opened = (_, _) => shown.TrySetResult(true);
        EventHandler closed = (_, _) => shown.TrySetResult(false);
        window.Opened += opened;
        window.Closed += closed;
        try
        {
            return await shown.Task.ConfigureAwait(true);
        }
        finally
        {
            window.Opened -= opened;
            window.Closed -= closed;
        }
    }
}
