using System.Globalization;
using CCISharp;

namespace XISOSharp;

/// <summary>
/// CCI naming and part discovery, matching <see cref="CciFile"/>: any numeric
/// part resolves the full contiguous set starting at <c>.1</c>, and reader
/// discovery accepts multi-digit part names such as <c>game.10.cci</c>.
/// </summary>
internal static class CciNaming
{
    /// <summary>
    /// Returns true when the path names a numbered CCI part
    /// (<c>game.1.cci</c>, <c>game.10.cci</c>, …).
    /// </summary>
    internal static bool IsSplitPath(string? path) =>
        TryParsePart(path, out _, out _);

    /// <summary>
    /// Enumerates existing parts for a CCI path. A numbered path resolves
    /// <c>.1.cci</c> upward; an unsuffixed <c>game.cci</c> resolves to itself.
    /// A numbered path outside the contiguous set is treated as a standalone
    /// image, matching <see cref="CciFile"/>.
    /// </summary>
    internal static List<string> DiscoverPartPaths(string path)
    {
        List<string> parts = new();
        if (!TryParsePart(path, out string baseName, out int requestedPart))
        {
            if (File.Exists(path)) parts.Add(path);
            return parts;
        }

        for (int index = 1; ; index++)
        {
            string candidate = $"{baseName}.{index}{CciFormat.Extension}";
            if (!File.Exists(candidate)) break;
            parts.Add(candidate);
        }

        if (parts.Count < requestedPart)
        {
            parts.Clear();
            if (File.Exists(path)) parts.Add(path);
            return parts;
        }

        ThrowIfPartGap(baseName, parts.Count);
        return parts;
    }

    /// <summary>
    /// Removes a trailing numeric part marker from a CCI path
    /// (<c>game.10.cci</c> → <c>game.cci</c>). Paths without a marker are
    /// returned unchanged.
    /// </summary>
    internal static string StripPartSuffix(string path)
    {
        if (!TryParsePart(path, out string baseName, out _)) return path;

        return baseName + path[^CciFormat.Extension.Length..];
    }

    private static bool TryParsePart(string? path, out string baseName, out int partNumber)
    {
        baseName = string.Empty;
        partNumber = 0;

        if (path is null || !path.EndsWith(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
            return false;

        string stem = path[..^CciFormat.Extension.Length];
        int dot = stem.LastIndexOf('.');
        if (dot < 0 || dot == stem.Length - 1) return false;

        string digits = stem[(dot + 1)..];
        if (digits.Length == 0 || digits[0] == '0') return false;

        foreach (char digit in digits)
        {
            if (digit is < '0' or > '9') return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out partNumber) ||
            partNumber < 1)
        {
            return false;
        }

        baseName = stem[..dot];
        return baseName.Length > 0;
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
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.cci"))
            {
                string name = Path.GetFileName(candidate);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(CciFormat.Extension, StringComparison.OrdinalIgnoreCase)) continue;

                string digits = name[prefix.Length..^CciFormat.Extension.Length];
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    continue;
                if (index > contiguousCount)
                {
                    throw new InvalidDataException(
                        $"CCI part {index} exists but part {contiguousCount + 1} is missing: {candidate}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The first part is missing; the caller reports not-found.
        }
    }

    /// <summary>Returns the total uncompressed length of a validated CCI part set.</summary>
    internal static bool TryGetLength(IReadOnlyList<string> parts, FileShare share, out long length)
    {
        length = 0;
        try
        {
            foreach (string part in parts)
            {
                if (!TryReadHeader(part, share, out CciHeader header)) return false;
                if (header.UncompressedSize > long.MaxValue) return false;

                length = checked(length + (long)header.UncompressedSize);
            }

            return true;
        }
        catch (OverflowException)
        {
            length = 0;
            return false;
        }
    }

    private static bool TryReadHeader(string path, FileShare share, out CciHeader header)
    {
        header = default;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, share, 64);
            Span<byte> buffer = stackalloc byte[CciFormat.HeaderSize];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer[read..]);
                if (n == 0) return false;
                read += n;
            }

            return CciHeader.TryRead(buffer, out header) == CciError.None;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
