namespace ZARSharp.Pipeline;

/// <summary>
/// Batch file listing. Ports <c>FileService.find_processable_files</c>
/// (ZarManager 1.2.0): non-recursive directory scan, lowercase-suffix match,
/// ordinal sort; unreadable entries are skipped.
/// </summary>
public static class ProcessableFiles
{
    /// <summary>Archive suffixes handled by the archive stage.</summary>
    public static IReadOnlySet<string> ArchiveExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ".zip", ".rar", ".7z", ".tar", ".gz" };

    /// <summary>Disc-image suffixes handled by the XISO stage.</summary>
    public static IReadOnlySet<string> IsoExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ".iso" };

    /// <summary>
    /// Lists processable entries of <paramref name="directory"/> for
    /// <paramref name="mode"/> (<c>Auto</c> accepts archives, ISOs and whole
    /// directories). Returns full paths, sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> Find(string directory, ZarProcessMode mode, Action<string>? log = null)
    {
        var found = new List<string>();
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(ex.Message);
            return found;
        }

        foreach (var entry in entries)
        {
            bool isFile;
            bool isDir;
            try
            {
                isFile = File.Exists(entry);
                isDir = !isFile && Directory.Exists(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var suffix = Path.GetExtension(entry).ToLowerInvariant();
            var accept = mode switch
            {
                ZarProcessMode.Auto =>
                    isDir || (isFile && (IsoExtensions.Contains(suffix) || ArchiveExtensions.Contains(suffix))),
                ZarProcessMode.ExtractArchive => isFile && ArchiveExtensions.Contains(suffix),
                ZarProcessMode.ExtractIso => isFile && IsoExtensions.Contains(suffix),
                ZarProcessMode.Compress => isDir,
                _ => false,
            };
            if (accept)
            {
                found.Add(entry);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
