using System.Globalization;

namespace XISOSharp;

/// <summary>
/// Split plain-ISO naming and part discovery for <c>game.1.iso</c>,
/// <c>game.2.iso</c>, … sets produced by <see cref="XisoSplitter"/>.
/// </summary>
internal static class SplitIsoFile
{
    /// <summary>Length of the <c>.1.iso</c> part suffix.</summary>
    private const int PartSuffixLength = 6;

    /// <summary>Returns true when <paramref name="path"/> is a <c>*.1.iso</c> first part.</summary>
    internal static bool IsSplitPath(string? path) =>
        path?.EndsWith(".1.iso", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Recovers the base name from a <c>*.1.iso</c> path.</summary>
    internal static string BaseName(string firstPartPath) => firstPartPath[..^PartSuffixLength];

    /// <summary>
    /// Enumerates consecutive existing parts (<c>.1.iso</c>, <c>.2.iso</c>, …),
    /// including multi-digit indices. Stops at the first missing part; an
    /// empty list means the first part does not exist.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A higher-numbered part exists after a missing one (a truncated or
    /// incomplete set would otherwise read as silent truncation).
    /// </exception>
    internal static List<string> DiscoverPartPaths(string firstPartPath)
    {
        List<string> parts = new();
        if (!IsSplitPath(firstPartPath)) return parts;

        string baseName = BaseName(firstPartPath);
        for (int index = 1; ; index++)
        {
            string candidate = $"{baseName}.{index}.iso";
            if (!File.Exists(candidate)) break;
            parts.Add(candidate);
        }

        ThrowIfPartGap(baseName, parts.Count);
        return parts;
    }

    /// <summary>Computes the combined length of every part in a split set.</summary>
    internal static long GetCombinedLength(IReadOnlyList<string> parts)
    {
        long length = 0;
        foreach (string part in parts)
        {
            length = checked(length + new FileInfo(part).Length);
        }

        return length;
    }

    private static void ThrowIfPartGap(string baseName, int contiguousCount)
    {
        string directory = Path.GetDirectoryName(baseName) ?? string.Empty;
        if (directory.Length == 0) directory = ".";
        string stem = Path.GetFileName(baseName);
        if (stem.Length == 0) return;

        string prefix = stem + ".";
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.iso"))
            {
                string name = Path.GetFileName(candidate);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) continue;

                string digits = name[prefix.Length..^4];
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
                if (index > contiguousCount)
                {
                    throw new InvalidDataException(
                        $"Split ISO part {index} exists but part {contiguousCount + 1} is missing: {candidate}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The first part is missing; the caller reports not-found.
        }
    }
}
