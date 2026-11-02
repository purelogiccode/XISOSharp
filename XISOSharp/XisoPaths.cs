namespace XISOSharp;

/// <summary>
/// Full-path comparison helpers behind the input==output safety guards
/// (TODO #15, xdvdfs #36): an output file must never silently overwrite one
/// of its inputs. Case sensitivity follows the OS convention (Windows/macOS
/// file systems are usually case-insensitive, Unix ones case-sensitive).
/// </summary>
public static class XisoPaths
{
    /// <summary>
    /// Case sensitivity for path comparisons: insensitive where the OS
    /// filesystem conventionally is (Windows/macOS), sensitive elsewhere.
    /// Shared by the pack/split collision guards so they agree with
    /// <see cref="AreSamePath"/> on every platform (BUG-LIB-025).
    /// </summary>
    internal static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Returns true when both paths resolve to the same file system entry.
    /// Returns false when either path is missing or cannot be resolved
    /// (unresolvable paths fail later with their own natural error).
    /// </summary>
    public static bool AreSamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        // Snapshot the working directory once: resolving the two paths against
        // different CWDs (a concurrent caller chdirs) would compare unrelated
        // absolute paths and miss an input==output collision.
        string? cwd = TryGetCurrentDirectory();
        string? fullA = TryResolve(a, cwd);
        string? fullB = TryResolve(b, cwd);
        if (fullA == null || fullB == null)
        {
            // At least one side is not a valid path: only identical spellings count.
            return string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
        }

        return string.Equals(fullA, fullB, PathComparison);
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> lies inside
    /// <paramref name="directory"/> (a trailing-separator-tolerant prefix match;
    /// <c>C:\src2\x</c> is not inside <c>C:\src</c>).
    /// </summary>
    public static bool IsWithinDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;

        // Same CWD snapshot as AreSamePath: both sides must resolve against one
        // working directory or a concurrent chdir can invert the containment.
        string? cwd = TryGetCurrentDirectory();
        string? full = TryResolve(path, cwd);
        string? dir = TryResolve(directory, cwd);
        if (full == null || dir == null || dir.Length == 0 || full.Length <= dir.Length)
            return false;

        // TrimTrailingSeparators keeps the separator on filesystem roots
        // (`C:\`, `\\server\share\`, `/`), so the character after the prefix is
        // already a name there (Todo #23).
        bool dirIsRoot = dir[^1] == Path.DirectorySeparatorChar || dir[^1] == Path.AltDirectorySeparatorChar;

        return full.StartsWith(dir, PathComparison) &&
               (dirIsRoot ||
                full[dir.Length] == Path.DirectorySeparatorChar ||
                full[dir.Length] == Path.AltDirectorySeparatorChar);
    }

    private static string? TryResolve(string path, string? baseDirectory)
    {
        try
        {
            string full = baseDirectory != null && !Path.IsPathRooted(path)
                ? Path.GetFullPath(path, baseDirectory)
                : Path.GetFullPath(path);
            return TrimTrailingSeparators(full);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException)
        {
            Logger.LogDebug($"Path resolve failed for '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the process working directory, or <c>null</c> when it cannot be
    /// read (for example after the directory was deleted on Unix).
    /// </summary>
    private static string? TryGetCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strips trailing directory separators without cutting into a filesystem
    /// root: <c>C:\</c>, <c>\\server\share\</c> and <c>/</c> are returned
    /// unchanged (naive <c>TrimEnd</c> would corrupt them into <c>C:</c>,
    /// <c>\\server\share</c> and the empty string). Batch-script <c>-d</c>
    /// values routinely carry trailing backslashes (upstream #61).
    /// </summary>
    public static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            return path;

        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Logger.LogDebug($"Path-root probe failed for '{path}': {ex.Message}");
            return trimmed;
        }

        int rootContentLength = (root ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        return trimmed.Length <= rootContentLength ? path : trimmed;
    }
}
