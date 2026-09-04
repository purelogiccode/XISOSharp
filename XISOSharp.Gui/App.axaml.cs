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
                _ = viewModel.InitializeAsync();
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
}