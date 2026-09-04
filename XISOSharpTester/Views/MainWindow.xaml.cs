using System.ComponentModel;
using System.Windows;
using Serilog;
using XISOSharpTester.Logging;
using XISOSharpTester.ViewModels;

namespace XISOSharpTester.Views;

/// <summary>
/// Main application window. Confirms with the user before closing while a test run is in progress.
/// </summary>
internal partial class MainWindow
{
    /// <summary>
    /// Initializes the main window and its XAML components.
    /// </summary>
    public MainWindow()
    {
        try
        {
            InitializeComponent();
            Log.Information("MainWindow initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainWindow initialization failed");
            BugReporter.ReportException(ex, "MainWindow initialization failed");
            throw;
        }
    }

    /// <summary>
    /// Prompts for confirmation when closing during an active test run; otherwise closes normally.
    /// </summary>
    /// <param name="e">Cancel event args; set to cancel when the user aborts the close.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            if (MainPageView.DataContext is MainViewModel { IsRunning: true })
            {
                var result = MessageBox.Show(
                    "A test run is currently in progress. Are you sure you want to exit?",
                    "Tests Running",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            base.OnClosing(e);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainWindow OnClosing failed");
            BugReporter.ReportException(ex, "MainWindow OnClosing failed");
            try { base.OnClosing(e); }
            catch
            {
                // ignored
            }
        }
    }
}