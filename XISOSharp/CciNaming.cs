using System.Globalization;
using CCISharp;

namespace XISOSharp;

/// <summary>
/// CCI naming and part discovery, matching <see cref="CciFile"/>: parts are
/// <c>game.1.cci</c> … <c>game.9.cci</c>, and any numbered part resolves the
/// full set starting at <c>.1</c>.
/// </summary>
internal static class CciNaming
{
    /// <summary>Length of a <c>.1.cci</c> part suffix.</summary>
    private const int PartSuffixLength = 6;

    /// <summary>Returns true when the path names a numbered CCI part (<c>.1.cci</c> … <c>.9.cci</c>).</summary>
    internal static bool IsSplitPath(string? path)
    {
        if (path is null || !path.EndsWith(CciFormat.Extension, StringComparison.OrdinalIgnoreCase)) return false;

        string stem = path[..^CciFormat.Extension.Length];
        return stem.Length >= 2 &&
               stem[^2] == '.' &&
               stem[^1] is >= '1' and <= '9';
    }

    /// <summary>
    /// Enumerates existing parts for a CCI path. A numbered path resolves
    /// <c>.1.cci</c> upward; an unsuffixed <c>game.cci</c> resolves to itself.
    /// </summary>
    internal static List<string> DiscoverPartPaths(string path)
    {
        List<string> parts = new();
        if (!IsSplitPath(path))
        {
            if (File.Exists(path)) parts.Add(path);
            return parts;
        }

        string baseName = path[..^(CciFormat.Extension.Length + 2)];
        for (int index = 1; index <= 9; index++)
        {
            string candidate = $"{baseName}.{index}{CciFormat.Extension}";
            if (!File.Exists(candidate)) break;
            parts.Add(candidate);
        }

        ThrowIfPartGap(baseName, parts.Count);
        return parts;
    }

    private static void ThrowIfPartGap(string baseName, int contiguousCount)
    {
        string directory = Path.GetDirectoryName(baseName) ?? string.Empty;
        if (directory.Length == 0) directory = ".";
        string stem = Path.GetFileName(baseName) ?? string.Empty;
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
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
                if (index > contiguousCount && index <= 9)
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
