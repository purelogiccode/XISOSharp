using System.Globalization;
using System.IO;
using System.Windows;
using Serilog;

namespace ExtractXisoTester;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExtractXisoTester", "logs", "extract-xiso-tester-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        Log.Information("ExtractXisoTester started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("ExtractXisoTester exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
