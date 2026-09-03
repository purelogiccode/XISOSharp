using Avalonia;
using System;
using System.Threading;
using XISOSharp.Gui.Services;

namespace XISOSharp.Gui;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
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

    private static int ProbeCli(string? overridePath)
    {
        var resolved = CliLocator.Resolve(overridePath);
        if (resolved is null)
        {
            Console.WriteLine("CLI not found (override, app folder, or PATH).");
            return 1;
        }

        Console.WriteLine($"CLI: {resolved}");
        var version = CliLocator.ProbeVersionAsync(resolved, CancellationToken.None).GetAwaiter().GetResult();
        if (version is null)
        {
            Console.WriteLine("CLI -v probe failed.");
            return 1;
        }

        Console.WriteLine($"Version: {version}");
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
