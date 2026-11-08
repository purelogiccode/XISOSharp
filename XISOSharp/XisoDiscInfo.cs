using System.Globalization;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Redump/XGD disc-layout inspector: reports the video and game partition
/// ranges, PFI/layer-break, embedded security-sector ranges and size
/// consistency for a plain XISO or Redump image. Container inputs (CISO, CCI,
/// split sets and GOD) report the game-partition view and mark video fields
/// unavailable.
/// </summary>
/// <remarks>
/// Only observed on-disk fields are reported. The geometry tables come from
/// <see cref="XgdTables"/> (ported from the permissively licensed
/// <c>XboxKit-0.7/LibXGD/XGD.cs</c>); PFI and security-sector parsing follows
/// the published field layout, not the GPL reference tool sources.
/// </remarks>
public static class XisoDiscInfo
{
    private const long SectorSize = Constants.SectorSize;

    /// <summary>Offset of the security-sector range count within a security sector.</summary>
    private const int SecurityRangeCountOffset = 1632;

    /// <summary>Offset of the first 9-byte security-sector range record.</summary>
    private const int SecurityRangeTableOffset = 1633;

    /// <summary>Maximum number of 9-byte range records a security sector can carry.</summary>
    private const int MaxSecurityRanges = 23;

    private static readonly string[] WaveLabels =
    [
        "XGD2 Wave 0", "XGD2 Wave 1", "XGD2 Wave 2", "XGD2 Wave 3", "XGD2 Wave 4",
        "XGD2 Wave 5", "XGD2 Wave 6", "XGD2 Wave 7", "XGD2 Wave 8", "XGD2 Wave 9",
        "XGD2 Wave 10", "XGD2 Wave 11", "XGD2 Wave 12", "XGD2 Wave 13", "XGD2 Wave 14",
        "XGD2 Wave 15", "XGD2 Wave 16", "XGD2 Wave 17", "XGD2 Wave 18", "XGD2 Wave 19",
        "XGD2 Wave 20", "XGD2 Hybrid", "XGD1", "XGD3 Beta",
    ];

    /// <summary>
    /// Reads the disc layout of <paramref name="path"/>.
    /// </summary>
    /// <param name="path">
    /// Plain ISO/XISO, split ISO, CISO/CCI container or GOD container (header
    /// file or its <c>.data</c> directory).
    /// </param>
    /// <param name="securitySectorsPath">
    /// Optional <c>sectors.txt</c> path used when the image carries no readable
    /// embedded security sector. When <c>null</c>, a sibling <c>sectors.txt</c>
    /// is used when present.
    /// </param>
    /// <param name="skipSectors">
    /// Optional explicit partition offset in 2048-byte sectors (Redump
    /// <c>--skip-sectors</c>); <c>null</c> probes the known layout offsets.
    /// </param>
    /// <param name="quiet">Suppress informational logging.</param>
    /// <returns>The inspected layout. Consistency problems are reported in
    /// <see cref="Models.XisoDiscLayout.Warnings"/> rather than thrown.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The image does not exist.</exception>
    /// <exception cref="InvalidDataException">The path is not a supported Xbox image.</exception>
    /// <exception cref="NotSupportedException">The path is a ZArchive (not a sector image).</exception>
    public static XisoDiscLayout Read(string path, string? securitySectorsPath = null, int? skipSectors = null,
        bool quiet = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Image not found: {path}", path);
        }

        if (!XisoImageSource.TryDetect(path, out XisoImageSourceInfo? source) || source is null)
        {
            throw new InvalidDataException($"Unsupported or invalid Xbox image: {path}");
        }

        if (source.Format == XisoImageFormat.Zar)
        {
            throw new NotSupportedException(
                "ZArchive (.zar) files are not sector images; use the list/extract/checksum verbs instead.");
        }

        List<string> warnings = [];
        bool isContainer = source.Format is not (XisoImageFormat.Iso or XisoImageFormat.SplitIso);
        long fileLength = source.Length;
        long totalSectors = fileLength / SectorSize;

        using Stream stream = XisoImageSource.OpenRead(path);
        VolumeInfo volume = XisoReader.GetVolumeInfo(stream, path, skipSectors);

        long discOffset = volume.IsValid
            ? volume.DiscLseek
            : skipSectors is >= 0
                ? skipSectors.Value * SectorSize
                : 0;

        int redumpType = XgdTables.GetRedumpIsoTypeBySize(fileLength);
        bool redumpSizeMatch = redumpType >= 0;
        string? redumpTypeName = redumpType >= 0 ? RedumpTypeName(redumpType) : null;

        int xgdType = redumpType >= 0
            ? XgdTables.GetXgdType(redumpType)
            : XgdTypeFromOffset(discOffset);
        string xgdTypeName = XgdTypeName(xgdType, redumpType, discOffset);

        int? videoType = null;
        string? videoTypeName = null;
        string? wave = null;
        if (!isContainer && redumpType >= 0)
        {
            int detectedVideoType = XgdTables.GetVideoType(stream, redumpType);
            if (detectedVideoType >= 0 && detectedVideoType < XgdTables.VideoL0Length.Length)
            {
                videoType = detectedVideoType;
                videoTypeName = VideoTypeName(detectedVideoType);
                int waveIndex = XgdTables.GetWave(stream, redumpType);
                if (waveIndex >= 0 && waveIndex < WaveLabels.Length)
                {
                    wave = WaveLabels[waveIndex];
                }
            }
            else
            {
                warnings.Add(
                    $"Video type could not be determined for Redump type {redumpType.ToString(CultureInfo.InvariantCulture)} " +
                    $"({redumpTypeName}); the PVD wave timestamp may be unknown.");
            }
        }

        DiscPartition? videoL0 = null;
        DiscPartition? videoL1 = null;
        if (videoType is { } vt)
        {
            long l0Length = XgdTables.VideoL0Length[vt];
            long l1Length = XgdTables.VideoL1Length[vt];
            bool l0Available = l0Length > 0 && l0Length <= fileLength;
            videoL0 = MakePartition("video L0", 0, l0Length, l0Available);
            if (!l0Available)
            {
                warnings.Add(
                    $"Image is truncated: video L0 ends at 0x{l0Length:X} but the file is only 0x{fileLength:X} bytes.");
            }

            long l1Start = fileLength - l1Length;
            bool l1Available = l1Length > 0 && l1Start >= 0 && l1Length <= fileLength;
            videoL1 = MakePartition("video L1", Math.Max(0, l1Start), l1Length, l1Available);
            if (!l1Available)
            {
                warnings.Add(
                    $"Image is truncated: video L1 needs 0x{l1Length:X} bytes at the end but the file is only " +
                    $"0x{fileLength:X} bytes.");
            }
        }

        DiscPartition? gamePartition = null;
        if (volume.IsValid)
        {
            long gameOffset = discOffset;
            long gameLength = fileLength > gameOffset ? fileLength - gameOffset : 0;
            if (redumpType >= 0)
            {
                long expectedLength = XgdTables.XisoLength[xgdType];
                long expectedEnd = gameOffset + expectedLength;
                if (expectedEnd > fileLength)
                {
                    warnings.Add(
                        $"Image is truncated: game partition ends at 0x{expectedEnd:X} but the file is only " +
                        $"0x{fileLength:X} bytes (missing 0x{expectedEnd - fileLength:X} bytes).");
                }
                // Bytes past the game partition are the layer-1 video tail;
                // that is expected for Redump images and reported separately.
                gameLength = Math.Min(expectedLength, Math.Max(0, fileLength - gameOffset));
            }

            gamePartition = MakePartition("game", gameOffset, gameLength,
                gameLength > 0 && gameOffset + gameLength <= fileLength);
        }
        else
        {
            warnings.Add("No XISO volume descriptor was found; game-partition fields are unavailable.");
        }

        DiscPhysicalFormatInfo? pfi = null;
        if (!isContainer && redumpType >= 0)
        {
            long pfiOffset = PfiOffset(xgdType, discOffset);
            if (pfiOffset >= 0 && pfiOffset + SectorSize <= fileLength)
            {
                byte[] sector = new byte[SectorSize];
                if (TryReadAt(stream, pfiOffset, sector) &&
                    TryParsePfi(sector, pfiOffset, out DiscPhysicalFormatInfo? parsedPfi) && parsedPfi is not null)
                {
                    pfi = parsedPfi;
                    if (pfi.Layer0EndSector == 0 || pfi.Layer0EndSector == 0xFFFFFF)
                    {
                        warnings.Add(
                            $"PFI at 0x{pfiOffset:X} has no usable layer-0 end sector (0x{pfi.Layer0EndSector:X}).");
                    }
                }
                else
                {
                    warnings.Add($"PFI sector at 0x{pfiOffset:X} is not readable or has an implausible layout.");
                }
            }
            else
            {
                warnings.Add($"PFI sector is outside the image (expected at 0x{pfiOffset:X}).");
            }
        }

        (string? securitySource, IReadOnlyList<DiscSecuritySectorRange> securityRanges) =
            ReadSecuritySectors(stream, path, isContainer, redumpType, xgdType, discOffset, fileLength,
                securitySectorsPath, warnings);

        if (!isContainer && redumpType >= 0)
        {
            if (volume.IsValid && discOffset != XgdTables.XisoOffset[xgdType])
            {
                warnings.Add(
                    $"Volume offset 0x{discOffset:X} does not match the expected 0x" +
                    $"{XgdTables.XisoOffset[xgdType]:X} for {xgdTypeName}.");
            }

            if (securityRanges.Count == 0)
            {
                warnings.Add(
                    "No embedded security-sector ranges were found and no readable sectors.txt was present.");
            }
        }
        else if (!isContainer && redumpType < 0 && discOffset != 0)
        {
            warnings.Add(
                $"Image uses the {xgdTypeName} layout offset but its length 0x{fileLength:X} is not a known " +
                "Redump image size; video/PFI/security fields may be absent.");
        }

        if (securityRanges.Count > 0)
        {
            long maxSector = fileLength / SectorSize;
            foreach (DiscSecuritySectorRange range in securityRanges)
            {
                if (range.StartSector >= maxSector || range.EndSector >= maxSector)
                {
                    warnings.Add(
                        $"Security-sector range {range.StartSector}-{range.EndSector} lies outside the image " +
                        $"({maxSector} sectors).");
                }
            }
        }

        if (!quiet)
        {
            Logger.Log(
                $"[INFO] {path}: {source.Format}, {xgdTypeName}, {totalSectors} sectors\n");
        }

        return new XisoDiscLayout(
            path,
            source.Format,
            isContainer,
            fileLength,
            totalSectors,
            volume,
            volume.IsValid ? volume.DiscFormat : "Unknown",
            xgdTypeName,
            redumpType >= 0 ? redumpType : null,
            redumpSizeMatch,
            redumpTypeName,
            videoType,
            videoTypeName,
            wave,
            videoL0,
            videoL1,
            gamePartition,
            pfi,
            securitySource,
            securityRanges,
            warnings);
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> describes a Redump-sized image
    /// (a known <see cref="XgdTables.RedumpIsoLength"/> value).
    /// </summary>
    /// <param name="path">Image path.</param>
    public static bool IsKnownRedumpSize(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path) && XgdTables.GetRedumpIsoTypeBySize(new FileInfo(path).Length) >= 0;
    }

    private static DiscPartition MakePartition(string name, long byteOffset, long byteLength, bool available) =>
        new(name, byteOffset, byteLength, byteOffset / SectorSize, byteLength / SectorSize, available);

    private static int XgdTypeFromOffset(long discOffset) => discOffset switch
    {
        0 => 0,
        Constants.GlobalLseekOffset => 1,
        Constants.Xgd2HybridLseekOffset => 2,
        Constants.Xgd3LseekOffset => 3,
        Constants.Xgd1LseekOffset => 0,
        _ => -1,
    };

    private static string XgdTypeName(int xgdType, int redumpType, long discOffset)
    {
        if (redumpType == 0)
        {
            return "XGD1 Beta";
        }

        if (xgdType >= 0 && xgdType <= 3 &&
            (redumpType >= 0 || discOffset is Constants.GlobalLseekOffset or Constants.Xgd2HybridLseekOffset
                or Constants.Xgd3LseekOffset or Constants.Xgd1LseekOffset))
        {
            return xgdType switch
            {
                0 => "XGD1",
                1 => "XGD2",
                2 => "XGD2 Hybrid",
                _ => "XGD3",
            };
        }

        return discOffset == 0 ? "RAW" : "Unknown";
    }

    private static string RedumpTypeName(int redumpType) => redumpType switch
    {
        0 => "XGD1 Beta",
        1 => "XGD1",
        2 => "XGD2 Wave 0",
        3 => "XGD2 Wave 1",
        4 => "XGD2 Wave 2",
        5 => "XGD2 Wave 3+",
        6 => "XGD2 Hybrid",
        7 => "XGD3 Beta/v0",
        8 => "XGD3",
        _ => "Unknown",
    };

    private static string VideoTypeName(int videoType) => videoType switch
    {
        0 => "XGD1 Beta",
        1 => "XGD1",
        2 => "XGD2 Wave 0",
        3 => "XGD2 Wave 1",
        4 => "XGD2 Wave 2",
        >= 5 and <= 14 => $"XGD2 Wave {videoType - 2}",
        15 => "XGD2 Hybrid",
        16 => "XGD3 Beta",
        17 => "XGD3 v0",
        18 => "XGD3",
        _ => $"Video type {videoType}",
    };

    private static long PfiOffset(int xgdType, long discOffset)
    {
        if (discOffset <= 0)
        {
            return -1;
        }

        return xgdType switch
        {
            2 => discOffset - 0x1800,
            3 => discOffset - 0x9800,
            _ => discOffset - 0x1800,
        };
    }

    private static long SecuritySectorOffset(int xgdType, long discOffset)
    {
        if (discOffset <= 0)
        {
            return -1;
        }

        return xgdType == 3 ? discOffset - 0x8800 : discOffset - 0x800;
    }

    private static bool TryParsePfi(ReadOnlySpan<byte> sector, long byteOffset, out DiscPhysicalFormatInfo? pfi)
    {
        pfi = null;
        if (sector.Length < 16)
        {
            return false;
        }

        int bookType = sector[0] >> 4;
        int partVersion = sector[0] & 0x0F;
        int discSize = sector[1] >> 4;
        int maximumRate = sector[1] & 0x0F;
        int layerCount = sector[2] >> 5;
        int trackPath = (sector[2] >> 4) & 1;
        int layerType = sector[2] & 0x0F;
        long dataStart = ReadUInt24BigEndian(sector[5..8]);
        long dataEnd = ReadUInt24BigEndian(sector[9..12]);
        long layer0End = ReadUInt24BigEndian(sector[13..16]);

        // A DVD PFI is 2048 bytes; only accept a sector with plausible field
        // values. Book type 0 (DVD-ROM) and part version 1 are observed on Xbox
        // media; layer counts are 1 or 2. The data-area bounds must be sane.
        if (discSize > 1 ||
            layerCount is < 1 or > 2 ||
            dataStart == 0xFFFFFF ||
            dataEnd == 0 ||
            dataEnd <= dataStart ||
            layer0End == 0xFFFFFF)
        {
            return false;
        }

        pfi = new DiscPhysicalFormatInfo(
            byteOffset,
            bookType,
            partVersion,
            discSize,
            maximumRate,
            layerCount,
            trackPath,
            layerType,
            dataStart,
            dataEnd,
            layer0End);
        return true;
    }

    private static (string? Source, IReadOnlyList<DiscSecuritySectorRange> Ranges) ReadSecuritySectors(
        Stream stream, string imagePath, bool isContainer, int redumpType, int xgdType, long discOffset,
        long fileLength, string? securitySectorsPath, List<string> warnings)
    {
        if (!isContainer && redumpType >= 0)
        {
            long offset = SecuritySectorOffset(xgdType, discOffset);
            if (offset >= 0 && offset + SectorSize <= fileLength)
            {
                byte[] sector = new byte[SectorSize];
                if (TryReadAt(stream, offset, sector) &&
                    TryParseEmbeddedSecuritySectors(sector, fileLength / SectorSize,
                        out IReadOnlyList<DiscSecuritySectorRange> embedded))
                {
                    return ("embedded", embedded);
                }

                warnings.Add($"Embedded security sector at 0x{offset:X} is not readable or is malformed.");
            }
            else
            {
                warnings.Add($"Embedded security sector is outside the image (expected at 0x{Math.Max(0, offset):X}).");
            }
        }

        string? path = securitySectorsPath;
        if (string.IsNullOrEmpty(path))
        {
            string sibling = Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, "sectors.txt");
            if (File.Exists(sibling))
            {
                path = sibling;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            return (null, []);
        }

        if (!File.Exists(path))
        {
            warnings.Add($"Security-sector list not found: {path}");
            return (null, []);
        }

        try
        {
            List<DiscSecuritySectorRange> ranges = [];
            long maxSector = fileLength / SectorSize;
            int lineNumber = 0;
            foreach (string raw in File.ReadLines(path))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split('-');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
                    !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
                {
                    warnings.Add($"Invalid security-sector line {lineNumber} in {path}: '{line}'.");
                    continue;
                }

                if (start < 0 || end < start || end - start != 4095)
                {
                    warnings.Add(
                        $"Invalid security-sector range '{line}' on line {lineNumber} of {path}: ranges must span " +
                        "exactly 4096 sectors.");
                    continue;
                }

                if (start >= maxSector || end >= maxSector)
                {
                    warnings.Add(
                        $"Security-sector range '{line}' on line {lineNumber} of {path} is outside the image.");
                }

                ranges.Add(new DiscSecuritySectorRange(start, end));
            }

            return ("sectors.txt", ranges);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read security-sector list {path}: {ex.Message}");
            return (null, []);
        }
    }

    private static bool TryParseEmbeddedSecuritySectors(ReadOnlySpan<byte> sector, long totalSectors,
        out IReadOnlyList<DiscSecuritySectorRange> ranges)
    {
        ranges = [];
        if (sector.Length < SecurityRangeTableOffset)
        {
            return false;
        }

        int count = sector[SecurityRangeCountOffset];
        if (count is < 1 or > MaxSecurityRanges)
        {
            return false;
        }

        List<DiscSecuritySectorRange> parsed = new(count);
        for (int i = 0; i < count; i++)
        {
            int offset = SecurityRangeTableOffset + (i * 9);
            if (offset + 9 > sector.Length)
            {
                break;
            }

            long start = ReadUInt24BigEndian(sector[(offset + 3)..(offset + 6)]);
            long end = ReadUInt24BigEndian(sector[(offset + 6)..(offset + 9)]);
            if (start == 0 && end == 0)
            {
                continue;
            }

            if (start < 0 || end < start || end - start != 4095)
            {
                return false;
            }

            parsed.Add(new DiscSecuritySectorRange(start, end));
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        _ = totalSectors;
        ranges = parsed;
        return true;
    }

    private static long ReadUInt24BigEndian(ReadOnlySpan<byte> value) =>
        ((long)value[0] << 16) | ((long)value[1] << 8) | value[2];

    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer[total..]);
                if (n == 0)
                {
                    return false;
                }

                total += n;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return false;
        }
    }
}
