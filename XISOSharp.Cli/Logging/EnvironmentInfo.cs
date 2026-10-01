using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

#if LOGGING_NS_GUI
namespace XISOSharp.Gui.Logging;
#elif LOGGING_NS_TESTER
namespace XISOSharpTester;
#else
namespace XISOSharp.Cli.Logging;
#endif

/// <summary>
/// Collects the environment block required on every bug report.
/// </summary>
internal static class EnvironmentInfo
{
    internal static string ApplicationVersion()
    {
        try
        {
            Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? asm.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(info) ? "Unknown" : info;
        }
        catch
        {
            return "Unknown";
        }
    }

    internal static bool IsTestHost()
    {
        try
        {
            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            if (entry?.Contains("test", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            if (AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    internal static string PlatformVersionLabel()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return "Windows Version";
            if (OperatingSystem.IsLinux())
                return "Linux Version";
            if (OperatingSystem.IsMacOS())
                return "MacOsX Version";
        }
        catch
        {
            // fall through
        }

        return "OS Version";
    }

    internal static string Collect(string applicationName)
    {
        string osVersion;
        string platformVersion;
        try
        {
            osVersion = Environment.OSVersion.ToString();
            platformVersion = RuntimeInformation.OSDescription;
        }
        catch
        {
            osVersion = "Unknown";
            platformVersion = "Unknown";
        }

        string architecture;
        try
        {
            architecture = $"{RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}";
        }
        catch
        {
            architecture = "Unknown";
        }

        string bitness;
        try
        {
            bitness = $"{(Environment.Is64BitProcess ? 64 : 32)}-bit (process), " +
                      $"{(Environment.Is64BitOperatingSystem ? 64 : 32)}-bit (OS)";
        }
        catch
        {
            bitness = "Unknown";
        }

        string baseDir;
        try
        {
            baseDir = AppContext.BaseDirectory;
        }
        catch
        {
            baseDir = "Unknown";
        }

        string tempPath;
        try
        {
            tempPath = Path.GetTempPath();
        }
        catch
        {
            tempPath = "Unknown";
        }

        StringBuilder sb = new();
        sb.AppendLine("=== Environment Details ===");
        sb.Append("Date: ").AppendLine(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("Application Name: ").AppendLine(applicationName);
        sb.Append("Application Version: ").AppendLine(ApplicationVersion());
        sb.Append("OS Version: ").AppendLine(osVersion);
        sb.Append("Architecture: ").AppendLine(architecture);
        sb.Append("Bitness: ").AppendLine(bitness);
        sb.Append(PlatformVersionLabel()).Append(": ").AppendLine(platformVersion);
        sb.Append("Processor Count: ").AppendLine(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("Base Directory: ").AppendLine(baseDir);
        sb.Append("Temp Path: ").Append(tempPath);
        return sb.ToString();
    }
}
