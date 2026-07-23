using System.IO;

namespace XISOSharpTester.Models;

public class XisoFileEntry
{
    public string FilePath { get; set; } = string.Empty;

    public string FileName => Path.GetFileName(FilePath);

    public string FileSize => new FileInfo(FilePath).Length switch
    {
        < 1024 => $"{new FileInfo(FilePath).Length} B",
        < 1024 * 1024 => $"{new FileInfo(FilePath).Length / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{new FileInfo(FilePath).Length / (1024.0 * 1024):F1} MB",
        _ => $"{new FileInfo(FilePath).Length / (1024.0 * 1024 * 1024):F2} GB"
    };

    public bool IsSmall => new FileInfo(FilePath).Length < 500_000_000L;
}
