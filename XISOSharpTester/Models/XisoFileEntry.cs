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
    /// the actual size of the file on disk. Returns <c>"0 B"</c> when the
    /// file is missing or cannot be statted (binding-safe, never throws).
    /// </summary>
    public string FileSize => FormatSize(TryGetLength());

    /// <summary>
    /// Gets whether the file is smaller than 500 MB and can be processed
    /// more quickly in test scenarios. Returns <c>false</c> when the file
    /// is missing or cannot be statted.
    /// </summary>
    public bool IsSmall => TryGetLength() is { } length && length < 500_000_000L;

    private long? TryGetLength()
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath))
                return null;
            var info = new FileInfo(FilePath);
            if (!info.Exists)
                return null;
            return info.Length;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    private static string FormatSize(long? length)
    {
        if (length is not { } v || v < 0)
            return "0 B";
        return v switch
        {
            < 1024 => $"{v} B",
            < 1024 * 1024 => $"{v / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{v / (1024.0 * 1024):F1} MB",
            _ => $"{v / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
