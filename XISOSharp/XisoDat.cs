using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace XISOSharp;

/// <summary>
/// One ROM entry from a Redump-style XML DAT (<c>game/rom</c> with size and
/// MD5/SHA-1/CRC hashes).
/// </summary>
/// <param name="GameName">The <c>game</c> name attribute (or empty).</param>
/// <param name="Name">The <c>rom</c> name attribute.</param>
/// <param name="Size">Declared byte size (0 when absent).</param>
/// <param name="Md5">Lowercase MD5 hex, when present.</param>
/// <param name="Sha1">Lowercase SHA-1 hex, when present.</param>
/// <param name="Crc">Lowercase CRC32 hex, when present.</param>
public sealed record DatRom(
    string GameName,
    string Name,
    long Size,
    string? Md5,
    string? Sha1,
    string? Crc);

/// <summary>Result of matching one supplied image against a DAT.</summary>
public enum DatMatchStatus
{
    /// <summary>Exactly one DAT entry matched every declared hash.</summary>
    Verified,

    /// <summary>More than one DAT entry matched; the DAT is ambiguous.</summary>
    Ambiguous,

    /// <summary>No DAT entry matched the image.</summary>
    NotInDat,
}

/// <summary>One supplied image and the DAT entries it matched.</summary>
/// <param name="ImagePath">Image passed to <see cref="XisoDat.Verify"/>.</param>
/// <param name="Status">Verification outcome.</param>
/// <param name="Size">Computed full-image size in bytes.</param>
/// <param name="Md5">Computed lowercase MD5.</param>
/// <param name="Sha1">Computed lowercase SHA-1.</param>
/// <param name="Crc">Computed lowercase CRC32.</param>
/// <param name="Matches">DAT entries that matched the image.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public verification payload: consumers and JSON output render every field.")]
public sealed record DatImageMatch(
    string ImagePath,
    DatMatchStatus Status,
    long Size,
    string Md5,
    string Sha1,
    string Crc,
    IReadOnlyList<DatRom> Matches);

/// <summary>Aggregate result of a <see cref="XisoDat.Verify"/> run.</summary>
/// <param name="DatPath">DAT file that was read.</param>
/// <param name="Images">One entry per supplied image, in input order.</param>
/// <param name="MissingRoms">DAT entries not matched by any supplied image.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public verification payload: consumers render the DAT path; the library only builds it.")]
public sealed record DatVerificationResult(
    string DatPath,
    IReadOnlyList<DatImageMatch> Images,
    IReadOnlyList<DatRom> MissingRoms)
{
    /// <summary>Number of images with exactly one match.</summary>
    public int Verified => Images.Count(static image => image.Status == DatMatchStatus.Verified);

    /// <summary>Number of images with more than one match.</summary>
    public int Ambiguous => Images.Count(static image => image.Status == DatMatchStatus.Ambiguous);

    /// <summary>Number of images with no match.</summary>
    public int NotInDat => Images.Count(static image => image.Status == DatMatchStatus.NotInDat);

    /// <summary>Number of DAT entries not matched by any image.</summary>
    public int Missing => MissingRoms.Count;

    /// <summary>True when every supplied image matched exactly one DAT entry.</summary>
    public bool Passed => Images.Count > 0 &&
                          Images.All(static image => image.Status == DatMatchStatus.Verified);
}

/// <summary>
/// Redump DAT verification: parses an XML DAT and matches images by full-image
/// size and hashes. No DAT source is built in; callers supply any Redump-style
/// XML file.
/// </summary>
public static class XisoDat
{
    /// <summary>
    /// Parses every <c>game/rom</c> (and <c>machine/rom</c>) entry from a
    /// Redump-style XML DAT.
    /// </summary>
    /// <param name="datPath">Path to the XML DAT file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entries in document order, without duplicate identical hashes.</returns>
    /// <exception cref="ArgumentException"><paramref name="datPath"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The DAT does not exist.</exception>
    /// <exception cref="InvalidDataException">The XML is malformed or is not a DAT.</exception>
    public static IReadOnlyList<DatRom> Parse(string datPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(datPath);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(datPath))
        {
            throw new FileNotFoundException($"DAT not found: {datPath}", datPath);
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        XDocument document;
        try
        {
            using XmlReader reader = XmlReader.Create(datPath, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Invalid DAT XML '{datPath}': {ex.Message}", ex);
        }

        XElement root = document.Root ?? throw new InvalidDataException($"Empty DAT XML: {datPath}");
        if (!string.Equals(root.Name.LocalName, "datafile", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root.Name.LocalName, "datfile", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"DAT root is '{root.Name.LocalName}', expected 'datafile' or 'datfile': {datPath}");
        }

        List<DatRom> roms = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement game in root.Elements().Where(static element =>
                     string.Equals(element.Name.LocalName, "game", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(element.Name.LocalName, "machine", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            string gameName = (string?)game.Attribute("name") ?? string.Empty;
            foreach (XElement rom in game.Elements().Where(static element =>
                         string.Equals(element.Name.LocalName, "rom", StringComparison.OrdinalIgnoreCase)))
            {
                string name = (string?)rom.Attribute("name") ?? string.Empty;
                if (name.Length == 0)
                {
                    continue;
                }

                long size = ParseSize((string?)rom.Attribute("size"));
                string? md5 = NormalizeHash((string?)rom.Attribute("md5"), 32);
                string? sha1 = NormalizeHash((string?)rom.Attribute("sha1"), 40);
                string? crc = NormalizeHash((string?)rom.Attribute("crc"), 8);
                if (md5 is null && sha1 is null && crc is null && size <= 0)
                {
                    // A declaration without size or hash cannot verify anything.
                    continue;
                }

                DatRom entry = new(gameName, name, size, md5, sha1, crc);
                string key = string.Join('|', entry.GameName, entry.Name, entry.Size,
                    entry.Md5 ?? string.Empty, entry.Sha1 ?? string.Empty, entry.Crc ?? string.Empty);
                if (seen.Add(key))
                {
                    roms.Add(entry);
                }
            }
        }

        if (roms.Count == 0)
        {
            throw new InvalidDataException($"DAT contains no usable game/rom entries: {datPath}");
        }

        return roms;
    }

    /// <summary>
    /// Matches each supplied image against <paramref name="datPath"/> by
    /// full-image size and MD5/SHA-1/CRC32. Each image is hashed once.
    /// </summary>
    /// <param name="datPath">Path to the XML DAT file.</param>
    /// <param name="imagePaths">Images to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">No image paths were supplied.</exception>
    public static DatVerificationResult Verify(string datPath, IReadOnlyList<string> imagePaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(imagePaths));
        }

        IReadOnlyList<DatRom> roms = Parse(datPath, ct);
        List<DatImageMatch> matches = new(imagePaths.Count);
        HashSet<DatRom> matchedRoms = [];
        foreach (string imagePath in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            XisoImageHashResult hash = XisoImageHash.Compute(imagePath, ct);
            List<DatRom> imageMatches = [];
            foreach (DatRom rom in roms)
            {
                if (rom.Size > 0 && rom.Size != hash.TotalBytes)
                {
                    continue;
                }

                bool hasHash = false;
                if (rom.Md5 is not null)
                {
                    hasHash = true;
                    if (!string.Equals(rom.Md5, hash.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (rom.Sha1 is not null)
                {
                    hasHash = true;
                    if (!string.Equals(rom.Sha1, hash.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (rom.Crc is not null)
                {
                    hasHash = true;
                    if (!string.Equals(rom.Crc, hash.Crc32, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (hasHash)
                {
                    imageMatches.Add(rom);
                }
            }

            foreach (DatRom rom in imageMatches)
            {
                matchedRoms.Add(rom);
            }

            DatMatchStatus status = imageMatches.Count switch
            {
                0 => DatMatchStatus.NotInDat,
                1 => DatMatchStatus.Verified,
                _ => DatMatchStatus.Ambiguous,
            };
            matches.Add(new DatImageMatch(imagePath, status, hash.TotalBytes, hash.Md5, hash.Sha1,
                hash.Crc32, imageMatches));
        }

        List<DatRom> missing = roms.Where(rom => !matchedRoms.Contains(rom)).ToList();
        return new DatVerificationResult(datPath, matches, missing);
    }

    private static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long size) && size >= 0
            ? size
            : 0;
    }

    private static string? NormalizeHash(string? value, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length != expectedLength)
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }
}
