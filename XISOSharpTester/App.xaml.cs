using System.Windows;
using Serilog;
using XISOSharpTester.Logging;

namespace XISOSharpTester;

/// <summary>
/// Application entry point for the XISOSharp Tester WPF application.
/// Configures Serilog logging (file/debug/console + Warning+ bug-report API),
/// writes the log to a rolling file in local app data, and logs startup
/// and shutdown events.
/// </summary>
public partial class App
{
    /// <summary>
    /// Configures Serilog logging before the application window opens.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            AppLogging.Configure("XISOSharpTester");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AppLogging.Configure failed: {ex.Message}");
        }

        try
        {
            base.OnStartup(e);
            Log.Information("XISOSharpTester started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Application startup failed");
            BugReporter.ReportException(ex, "Application startup failed");
            throw;
        }
    }

    /// <summary>
    /// Logs the shutdown event and flushes the Serilog log before
    /// the application exits.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("XISOSharpTester exiting");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnExit logging failed: {ex.Message}");
        }
        finally
        {
            AppLogging.CloseAndFlush();
            try
            {
                base.OnExit(e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnExit failed: {ex.Message}");
            }
        }
    }
}
