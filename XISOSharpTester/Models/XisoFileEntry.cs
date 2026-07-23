using System.IO;

namespace XISOSharpTester.Models;

/// <summary>
/// Represents a single XISO disc image file selected for testing.
/// Provides computed properties for the file name, formatted size,
/// and whether it qualifies as a "small" file for quick processing.
/// </summary>
public class XisoFileEntry
{
    /// <summary>
    /// Gets or sets the full path to the XISO file on disk.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets the file name (without directory) derived from <see cref="FilePath"/>.
    /// </summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// Gets the human-readable file size string (e.g. "1.5 MB") derived from
    /// the actual size of the file on disk.
    /// </summary>
    public string FileSize => new FileInfo(FilePath).Length switch
    {
        < 1024 => $"{new FileInfo(FilePath).Length} B",
        < 1024 * 1024 => $"{new FileInfo(FilePath).Length / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{new FileInfo(FilePath).Length / (1024.0 * 1024):F1} MB",
        _ => $"{new FileInfo(FilePath).Length / (1024.0 * 1024 * 1024):F2} GB"
    };

    /// <summary>
    /// Gets whether the file is smaller than 500 MB and can be processed
    /// more quickly in test scenarios.
    /// </summary>
    public bool IsSmall => new FileInfo(FilePath).Length < 500_000_000L;
}
