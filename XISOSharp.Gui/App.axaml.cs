using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.ViewModels;
using XISOSharp.Gui.Views;

namespace XISOSharp.Gui;

/// <summary>
/// Avalonia application. Loads XAML resources and wires the main window to a fresh
/// <see cref="ViewModels.MainViewModel"/> on desktop startup.
/// </summary>
public class App : Application
{
    private Task? _startupTask;

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
            BugReporter.ReportException(ex, "App.Initialize failed");
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
                var viewModel = new MainViewModel();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel,
                };
                _startupTask = StartupAsync(viewModel);
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "App startup failed");
            BugReporter.ReportException(ex, "App startup failed");
            throw;
        }
    }

    private static async Task StartupAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI startup initialization failed");
            BugReporter.ReportException(ex, "GUI startup initialization failed");
            try
            {
                viewModel.LogMessage($"[GUI] Startup initialization failed: {ex.Message}");
            }
            catch
            {
                // ignored
            }
        }
    }
}
