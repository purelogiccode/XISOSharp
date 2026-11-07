using System.Globalization;
using System.Text.Json;

namespace XISOSharp.Models;

#pragma warning disable MA0048 // Related layout types are grouped intentionally.

/// <summary>
/// One fixed extent in an explicit XISO sector layout (PlanToEnhance Phase 8).
/// Sectors are partition-relative, matching <see cref="FileSectorExtent.StartSector"/>
/// and <see cref="EntryInfo.StartSector"/>.
/// </summary>
/// <param name="Path">
/// Image-internal path with a leading slash and <c>/</c> separators
/// (<c>"/"</c> for the root table, <c>"/sub/file.bin"</c> for a file).
/// </param>
/// <param name="StartSector">First sector reserved for the extent.</param>
/// <param name="SectorCount">
/// Sectors reserved for the extent, or <c>null</c> when the compact text form
/// omits it (the packer then uses the source entry's actual size).
/// </param>
/// <param name="FileSize">
/// Declared byte size, or <c>null</c> when the compact text form omits it.
/// For directories this is the directory-table byte size.
/// </param>
/// <param name="IsDirectory">
/// Whether the extent is a directory table, or <c>null</c> when the compact
/// text form omits it.
/// </param>
public sealed record XisoLayoutEntry(
    string Path,
    uint StartSector,
    uint? SectorCount,
    uint? FileSize,
    bool? IsDirectory);

/// <summary>
/// An explicit sector layout accepted by layout-aware packing (PlanToEnhance
/// Phase 8, xdvdfs #49). Load one from the JSON emitted by
/// <c>--sector-layout --json</c>/<see cref="XisoReader.GetSectorLayout(string)"/>
/// or from a compact <c>path=sector</c> text file, then pass it to
/// <c>XisoWriter.CreateXiso</c> or <c>XisoWriter.PackFromDirectory</c>.
/// </summary>
/// <remarks>
/// Every entry is a fixed extent: the packer writes the matching file or
/// directory table at exactly that sector. Source entries not present in the
/// layout are allocated first-fit around the fixed extents. Overlaps, extents
/// that are too small, and extents outside <see cref="TotalSectors"/> fail with
/// an <see cref="InvalidDataException"/> before any output is written.
/// </remarks>
public sealed class XisoLayoutSpec
{
    private XisoLayoutSpec(IReadOnlyList<XisoLayoutEntry> entries, long? totalSectors, bool strictPaths)
    {
        Entries = entries;
        TotalSectors = totalSectors;
        StrictPaths = strictPaths;
    }

    /// <summary>Fixed extents, in the order they were declared.</summary>
    public IReadOnlyList<XisoLayoutEntry> Entries { get; }

    /// <summary>
    /// Partition sector count to preserve, or <c>null</c> to size the image to
    /// the last allocation. The writer pads the output to 64 KiB afterwards.
    /// </summary>
    public long? TotalSectors { get; }

    /// <summary>
    /// <c>true</c> for the compact <c>path=sector</c> text form: every declared
    /// path must exist in the source tree. JSON layouts are not strict — extra
    /// extents are reserved so an image can be repacked with fewer files.
    /// </summary>
    public bool StrictPaths { get; }

    /// <summary>
    /// Creates a layout from an image's parsed <see cref="SectorLayout"/>
    /// (the in-memory form of <c>--sector-layout</c>).
    /// </summary>
    /// <param name="layout">Layout to convert.</param>
    /// <returns>A non-strict layout with every extent fixed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="layout"/> is <c>null</c>.</exception>
    public static XisoLayoutSpec FromSectorLayout(SectorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        List<XisoLayoutEntry> entries = new(layout.Entries.Count);
        foreach (FileSectorExtent extent in layout.Entries)
        {
            entries.Add(new XisoLayoutEntry(
                NormalizePath(extent.Path, "sector layout"),
                extent.StartSector,
                extent.SectorCount,
                extent.FileSize,
                extent.IsDirectory));
        }

        return new XisoLayoutSpec(entries, layout.TotalSectors, strictPaths: false);
    }

    /// <summary>
    /// Loads a layout from a file. JSON is detected by the first non-whitespace
    /// character (<c>{</c> or <c>[</c>); anything else is parsed as the compact
    /// <c>path=sector</c> text form.
    /// </summary>
    /// <param name="path">Layout file to read.</param>
    /// <returns>The parsed layout.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="FormatException">Thrown when the content is not a valid layout.</exception>
    /// <exception cref="IOException">Thrown on read errors.</exception>
    public static XisoLayoutSpec Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Sector layout file not found: {path}", path);

        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses a layout from text, detecting JSON versus the compact text form.
    /// </summary>
    /// <param name="text">Layout content.</param>
    /// <returns>The parsed layout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <c>null</c>.</exception>
    /// <exception cref="FormatException">Thrown when the content is not a valid layout.</exception>
    public static XisoLayoutSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        text = StripBom(text);
        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')
            ? ParseJson(text)
            : ParseText(text);
    }

    /// <summary>
    /// Parses the JSON emitted by <c>--sector-layout --json</c> (or any object
    /// with an <c>entries</c> array and an optional <c>totalSectors</c>).
    /// Property names are matched case-insensitively; <c>startSector</c>/<c>sector</c>,
    /// <c>sectorCount</c>/<c>count</c>, and <c>size</c>/<c>fileSize</c> are accepted.
    /// </summary>
    /// <param name="json">JSON document.</param>
    /// <returns>The parsed layout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is <c>null</c>.</exception>
    /// <exception cref="FormatException">Thrown when the JSON is malformed or an entry is invalid.</exception>
    public static XisoLayoutSpec ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        json = StripBom(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid sector-layout JSON: {ex.Message}", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            JsonElement entriesElement;
            long? totalSectors = null;

            if (root.ValueKind == JsonValueKind.Array)
            {
                entriesElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(root, "volume", out JsonElement volume) &&
                    volume.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(volume, "totalSectors", out JsonElement volumeTotal))
                {
                    totalSectors = ReadTotalSectors(volumeTotal, "volume.totalSectors");
                }
                else if (TryGetProperty(root, "totalSectors", out JsonElement rootTotal))
                {
                    totalSectors = ReadTotalSectors(rootTotal, "totalSectors");
                }

                if (!TryGetProperty(root, "entries", out entriesElement))
                {
                    throw new FormatException(
                        "Invalid sector-layout JSON: missing the 'entries' array.");
                }
            }
            else
            {
                throw new FormatException(
                    "Invalid sector-layout JSON: the root must be an object or an array of entries.");
            }

            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException(
                    "Invalid sector-layout JSON: 'entries' must be an array.");
            }

            List<XisoLayoutEntry> entries = new();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (JsonElement element in entriesElement.EnumerateArray())
            {
                entries.Add(ParseJsonEntry(element, index, seen));
                index++;
            }

            return new XisoLayoutSpec(entries, totalSectors, strictPaths: false);
        }
    }

    /// <summary>
    /// Parses the compact text form. One <c>path=sector</c> per line; blank
    /// lines and lines starting with <c>#</c> or <c>;</c> are ignored.
    /// <c>total-sectors=N</c> (or <c>totalSectors=N</c>) optionally pins the
    /// output size. Sectors accept decimal or <c>0x</c> hexadecimal.
    /// </summary>
    /// <param name="text">Text layout.</param>
    /// <returns>The parsed layout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <c>null</c>.</exception>
    /// <exception cref="FormatException">Thrown when a line is invalid or a path repeats.</exception>
    public static XisoLayoutSpec ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        text = StripBom(text);
        List<XisoLayoutEntry> entries = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long? totalSectors = null;

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            int equals = line.LastIndexOf('=');
            if (equals <= 0 || equals == line.Length - 1)
            {
                throw new FormatException(
                    $"Invalid sector layout at line {lineNumber}: expected 'path=sector' or 'total-sectors=N'.");
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Equals("total-sectors", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("totalsectors", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("total_sectors", StringComparison.OrdinalIgnoreCase))
            {
                totalSectors = ParseSectorCount(value, lineNumber, "total-sectors");
                continue;
            }

            uint sector = ParseSector(value, lineNumber);
            string path = NormalizePath(key, $"line {lineNumber}");
            if (!seen.Add(path))
            {
                throw new FormatException(
                    $"Invalid sector layout at line {lineNumber}: duplicate path '{path}'.");
            }

            entries.Add(new XisoLayoutEntry(path, sector, null, null, null));
        }

        return new XisoLayoutSpec(entries, totalSectors, strictPaths: true);
    }

    private static XisoLayoutEntry ParseJsonEntry(JsonElement element, int index, HashSet<string> seen)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: entry {index} must be an object.");
        }

        if (!TryGetProperty(element, "path", out JsonElement pathElement) ||
            pathElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: entry {index} is missing a string 'path'.");
        }

        string path = NormalizePath(pathElement.GetString() ?? string.Empty, $"entry {index}");

        if (!TryGetProperty(element, "startSector", out JsonElement startElement) &&
            !TryGetProperty(element, "sector", out startElement))
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: entry '{path}' is missing 'startSector'.");
        }

        uint startSector = ReadUInt32(startElement, $"entry '{path}' startSector");

        uint? sectorCount = null;
        if (TryGetProperty(element, "sectorCount", out JsonElement countElement) ||
            TryGetProperty(element, "count", out countElement))
        {
            sectorCount = ReadUInt32(countElement, $"entry '{path}' sectorCount");
        }

        uint? fileSize = null;
        if (TryGetProperty(element, "size", out JsonElement sizeElement) ||
            TryGetProperty(element, "fileSize", out sizeElement))
        {
            fileSize = ReadUInt32(sizeElement, $"entry '{path}' size");
        }

        bool? isDirectory = null;
        if (TryGetProperty(element, "isDirectory", out JsonElement dirElement))
        {
            if (dirElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException(
                    $"Invalid sector-layout JSON: entry '{path}' has a non-boolean 'isDirectory'.");
            }

            isDirectory = dirElement.GetBoolean();
        }

        if (sectorCount == null && fileSize == null)
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: entry '{path}' has neither 'sectorCount' nor 'size'.");
        }

        if (sectorCount == null)
        {
            sectorCount = SectorCountForBytes(fileSize!.Value);
        }

        if (!seen.Add(path))
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: duplicate path '{path}'.");
        }

        return new XisoLayoutEntry(path, startSector, sectorCount, fileSize, isDirectory);
    }

    private static uint SectorCountForBytes(uint byteCount) =>
        byteCount == 0 ? 0 : (byteCount / Constants.SectorSize) +
                             (byteCount % Constants.SectorSize == 0 ? 0u : 1u);

    private static long ReadTotalSectors(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value) || value <= 0)
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: '{context}' must be a positive integer.");
        }

        return value;
    }

    private static uint ReadUInt32(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out uint value))
        {
            throw new FormatException(
                $"Invalid sector-layout JSON: '{context}' must be a non-negative 32-bit integer.");
        }

        return value;
    }

    private static uint ParseSector(string value, int lineNumber)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out uint hex))
        {
            return hex;
        }

        if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
            return dec;

        throw new FormatException(
            $"Invalid sector layout at line {lineNumber}: '{value}' is not a sector number.");
    }

    private static long ParseSectorCount(string value, int lineNumber, string field)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        throw new FormatException(
            $"Invalid sector layout at line {lineNumber}: '{field}' must be a positive integer.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string StripBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    /// <summary>
    /// Normalizes an image-internal path to the canonical form used for matching:
    /// <c>/</c> separators, a leading slash, no duplicate or trailing separators,
    /// and no <c>.</c>/<c>..</c> segments.
    /// </summary>
    private static string NormalizePath(string path, string context)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FormatException(
                $"Invalid sector layout ({context}): the path must not be empty.");
        }

        string replaced = path.Trim().Replace('\\', '/');
        if (!replaced.StartsWith('/'))
            replaced = "/" + replaced;

        string[] segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "/";

        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new FormatException(
                    $"Invalid sector layout ({context}): path '{path}' contains a '{segment}' segment.");
            }
        }

        return "/" + string.Join('/', segments);
    }
}
