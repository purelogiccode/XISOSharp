using System.Globalization;
using Serilog;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester.Logging;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Single Serilog bootstrap for the CLI. Configures file/debug/console sinks plus
/// the <see cref="BugReportSink"/> (Error+ -&gt; bug-report API), bridges the
/// shared <c>XISOSharp.Logger</c> through Serilog, and installs global crash handlers.
/// </summary>
internal static class AppLogging
{
    private static bool _configured;
#if NET9_0_OR_GREATER
    private static readonly Lock Gate = new();
#else
    private static readonly object Gate = new();
#endif

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
            string? dir = Path.GetDirectoryName(logPath);
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
        // Qualified (not bare `Logger`) so this shared source compiles under the
        // CLI, GUI, and Tester logging namespaces alike (BUG-X-001).
        Logger.ForwardInfo = msg => Log.Information("{Message}", msg.TrimEnd('\r', '\n'));
        Logger.ForwardError = msg =>
        {
            // BUG-X-004: every Logger.LogErr write is user-facing feedback —
            // usage/validation refusals (invalid flag combinations, misplaced
            // flags), missing-file probes, and non-zero CLI exits. Filing each
            // one as a bug report flooded the API with 32 expected-behaviour
            // reports in a single session. They are exactly the "routine
            // operational noise" BUG-X-002 already excludes, so log them as
            // Warning: the file log keeps them, while the BugReportSink
            // (Error+) stays reserved for real crashes, which reach the
            // reporter through the unhandled-exception handlers and explicit
            // ReportException call sites instead.
            string text = msg.TrimEnd('\r', '\n');
            Log.Warning("{Message}", text);
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
                try
                {
                    Log.CloseAndFlush();
                }
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

        Log.Information("{App} logging initialized (version {Version})", applicationName,
            EnvironmentInfo.ApplicationVersion());
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
