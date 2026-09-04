using Avalonia;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.Services;

namespace XISOSharp.Gui;

/// <summary>
/// GUI entry point. Handles headless helpers (<c>--self-test</c>, <c>--probe-cli</c>,
/// <c>--help</c>) and otherwise starts the Avalonia desktop lifetime.
/// </summary>
internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>
    /// Starts the GUI or runs a headless helper, depending on <paramref name="args"/>.
    /// </summary>
    /// <param name="args"><c>--self-test</c>, <c>--probe-cli</c>, <c>--help</c>, or empty to launch the UI.</param>
    /// <returns>0 on success; 1 when the CLI probe fails or usage is invalid.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        AppLogging.Configure("XISOSharp.Gui");
        try
        {
            return MainInner(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled GUI exception");
            BugReporter.ReportException(ex, "Unhandled GUI exception");
            return 1;
        }
        finally
        {
            AppLogging.CloseAndFlush();
        }
    }

    private static int MainInner(string[] args)
    {
        // Headless helpers so the GUI wrapper is verifiable without a display:
        //   --probe-cli [path]  resolve the CLI and print its -v banner line
        //   --self-test         verify every argv builder, print PASS/FAIL
        //   --help | -h         print usage
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTest.Run(Console.WriteLine, args.Length > 1 ? args[1] : null);
            }

            if (string.Equals(args[0], "--probe-cli", StringComparison.OrdinalIgnoreCase))
            {
                return ProbeCli(args.Length > 1 ? args[1] : null);
            }

            if (string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("XISOSharp.Gui — dark desktop front-end that drives the XISOSharp CLI.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  XISOSharp.Gui [--probe-cli [cliPath]] [--self-test [cliPath]]");
                Console.WriteLine();
                Console.WriteLine("With no arguments the graphical interface starts.");
                return 0;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Resolves the CLI and prints its location plus <c>-v</c> banner line.
    /// </summary>
    /// <param name="overridePath">Optional explicit CLI path; otherwise auto-detection is used.</param>
    /// <returns>0 when the CLI is found and responds to <c>-v</c>; otherwise 1.</returns>
    private static int ProbeCli(string? overridePath)
    {
        try
        {
            var resolved = CliLocator.Resolve(overridePath);
            if (resolved is null)
            {
                Console.WriteLine("CLI not found (override, app folder, or PATH).");
                Log.Warning("CLI probe: not found (override, app folder, or PATH)");
                return 1;
            }

            Console.WriteLine($"CLI: {resolved}");
            var version = CliLocator.ProbeVersionAsync(resolved, CancellationToken.None).GetAwaiter().GetResult();
            if (version is null)
            {
                Console.WriteLine("CLI -v probe failed.");
                Log.Warning("CLI -v probe failed for {Cli}", resolved);
                BugReporter.ReportWarning($"CLI -v probe failed for {resolved}");
                return 1;
            }

            Console.WriteLine($"Version: {version}");
            Log.Information("CLI probe OK: {Cli} ({Version})", resolved, version);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI probe failed");
            BugReporter.ReportException(ex, "CLI probe failed");
            Console.WriteLine($"CLI probe failed: {ex.Message}");
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    /// <summary>
    /// Configures the Avalonia application builder.
    /// </summary>
    /// <returns>The configured Avalonia <c>AppBuilder</c>.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        try
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BuildAvaloniaApp failed");
            BugReporter.ReportException(ex, "BuildAvaloniaApp failed");
            throw;
        }
    }
}