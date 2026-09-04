using System.Globalization;
using Serilog;

namespace XISOSharp.Gui.Logging;

/// <summary>
/// Single Serilog bootstrap for the GUI. Configures file/debug/console sinks plus
/// the <see cref="BugReportSink"/> (Warning+ -&gt; bug-report API), bridges the
/// shared <c>XISOSharp.Logger</c> through Serilog, and installs global crash handlers.
/// </summary>
internal static class AppLogging
{
    private static bool _configured;
    private static readonly Lock Gate = new();

    internal static void Configure(string applicationName)
    {
        lock (Gate)
        {
            if (_configured)
            {
                BugReporter.ApplicationName = applicationName;
                return;
            }

            _configured = true;
        }

        BugReporter.ApplicationName = applicationName;

        string logPath;
        try
        {
            logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                applicationName, "logs", $"{applicationName}-.log");
        }
        catch
        {
            logPath = Path.Combine(Path.GetTempPath(), applicationName, "logs", $"{applicationName}-.log");
        }

        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
                _ = Directory.CreateDirectory(dir);
        }
        catch
        {
            // Best effort — Serilog will fall back to remaining sinks.
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(logPath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
                formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Sink(new BugReportSink())
            .CreateLogger();

        // Route every XISOSharp.Logger write through Serilog as well.
        // Console output is preserved (Logger still writes to Out/Error);
        // Serilog adds file/debug/bug-report coverage for the same text.
        Logger.ForwardInfo = msg => Log.Information("{Message}", msg.TrimEnd('\r', '\n'));
        Logger.ForwardError = msg =>
        {
            var text = msg.TrimEnd('\r', '\n');
            if (msg.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))
                Log.Warning("{Message}", text);
            else
                Log.Error("{Message}", text);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Log.Fatal(ex, "Unhandled exception in {App}", applicationName);
                    BugReporter.ReportException(ex, $"Unhandled exception in {applicationName}");
                }
                else
                {
                    Log.Fatal("Unhandled non-exception in {App}: {Object}", applicationName, e.ExceptionObject);
                    BugReporter.ReportError($"Unhandled non-exception in {applicationName}: {e.ExceptionObject}");
                }
            }
            catch
            {
                // Never throw from a crash handler.
            }
            finally
            {
                try { Log.CloseAndFlush(); }
                catch
                {
                    // ignored
                }
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Log.Error(e.Exception, "Unobserved task exception in {App}", applicationName);
                BugReporter.ReportException(e.Exception, $"Unobserved task exception in {applicationName}");
                e.SetObserved();
            }
            catch
            {
                // Never throw from a crash handler.
            }
        };

        Log.Information("{App} logging initialized (version {Version})", applicationName, EnvironmentInfo.ApplicationVersion());
    }

    internal static void CloseAndFlush()
    {
        try
        {
            Log.CloseAndFlush();
        }
        catch
        {
            // Best effort.
        }
    }
}

