namespace XISOSharp;

/// <summary>
/// Full-path comparison helpers behind the input==output safety guards
/// (TODO #15, xdvdfs #36): an output file must never silently overwrite one
/// of its inputs. Case sensitivity follows the OS convention (Windows/macOS
/// file systems are usually case-insensitive, Unix ones case-sensitive).
/// </summary>
public static class XisoPaths
{
    private static readonly StringComparison PathComparison =
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

        var fullA = TryResolve(a);
        var fullB = TryResolve(b);
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

        var full = TryResolve(path);
        var dir = TryResolve(directory);
        if (full == null || dir == null || dir.Length == 0 || full.Length <= dir.Length)
            return false;

        return full.StartsWith(dir, PathComparison) &&
               (full[dir.Length] == Path.DirectorySeparatorChar ||
                full[dir.Length] == Path.AltDirectorySeparatorChar);
    }

    private static string? TryResolve(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
