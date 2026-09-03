using System.Globalization;
using System.IO;
using System.Windows;
using Serilog;

namespace XISOSharpTester;

/// <summary>
/// Application entry point for the XISOSharp Tester WPF application.
/// Configures Serilog logging, writes the log to a rolling file
/// in local app data, and logs startup and shutdown events.
/// </summary>
public partial class App
{
    /// <summary>
    /// Configures Serilog logging before the application window opens.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XISOSharpTester", "logs", "extract-xiso-tester-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(logPath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("XISOSharpTester started");
    }

    /// <summary>
    /// Logs the shutdown event and flushes the Serilog log before
    /// the application exits.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("XISOSharpTester exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}