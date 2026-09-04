using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace XISOSharp.Cli.Logging;

/// <summary>
/// Collects the environment block required on every bug report.
/// </summary>
internal static class EnvironmentInfo
{
    internal static string ApplicationVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(info) ? "Unknown" : info;
        }
        catch
        {
            return "Unknown";
        }
    }

    internal static string Collect(string applicationName)
    {
        string osVersion;
        string windowsVersion;
        try
        {
            osVersion = Environment.OSVersion.ToString();
            windowsVersion = RuntimeInformation.OSDescription;
        }
        catch
        {
            osVersion = "Unknown";
            windowsVersion = "Unknown";
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

        var sb = new StringBuilder();
        sb.AppendLine("=== Environment Details ===");
        sb.Append("Date: ").AppendLine(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("Application Name: ").AppendLine(applicationName);
        sb.Append("Application Version: ").AppendLine(ApplicationVersion());
        sb.Append("OS Version: ").AppendLine(osVersion);
        sb.Append("Architecture: ").AppendLine(architecture);
        sb.Append("Bitness: ").AppendLine(bitness);
        sb.Append("Windows Version: ").AppendLine(windowsVersion);
        sb.Append("Processor Count: ").AppendLine(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("Base Directory: ").AppendLine(baseDir);
        sb.Append("Temp Path: ").Append(tempPath);
        return sb.ToString();
    }
}
