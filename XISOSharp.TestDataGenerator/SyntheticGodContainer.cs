using System.Buffers.Binary;
using System.Text;

namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Builds a deterministic, copyright-free GOD/SVOD container from an existing
/// XISO image. The header carries the XContent title block; the .data fragment
/// stores the image at the synthetic GOD layout's sector base. This is test
/// data only: it is not a signed retail container and contains no game data.
/// </summary>
public static class SyntheticGodContainer
{
    private const uint SignatureLive = 0x4C495645;
    private const uint ContentTypeGod = 0x7000;

    /// <summary>
    /// Creates a GOD container beside <paramref name="headerPath"/>.
    /// </summary>
    /// <param name="isoPath">Sector-aligned XISO image to wrap.</param>
    /// <param name="headerPath">Header path (no extension recommended).</param>
    /// <param name="titleName">XContent title name.</param>
    /// <param name="titleId">XContent title id.</param>
    /// <param name="discNumber">Disc number (1-based).</param>
    /// <param name="discCount">Disc count.</param>
    /// <returns>The header path.</returns>
    /// <exception cref="InvalidDataException">The image is empty or not sector-aligned.</exception>
    public static string CreateFromIso(
        string isoPath,
        string headerPath,
        string titleName = "Synthetic GOD",
        uint titleId = 0x4D5307E6,
        byte discNumber = 1,
        byte discCount = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(isoPath);
        ArgumentException.ThrowIfNullOrEmpty(headerPath);

        byte[] image = File.ReadAllBytes(isoPath);
        if (image.Length == 0 || image.Length % Constants.SectorSize != 0)
        {
            throw new InvalidDataException(
                $"GOD fixture requires a non-empty sector-aligned ISO; '{isoPath}' is {image.Length} bytes.");
        }

        int sectors = image.Length / Constants.SectorSize;
        uint dataBlocks = (uint)((sectors + 1) / 2);
        byte[] header = BuildHeader(titleName, titleId, discNumber, discCount, dataBlocks);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(headerPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(headerPath, header);

        string dataDirectory = headerPath + ".data";
        Directory.CreateDirectory(dataDirectory);
        string fragment = Path.Combine(dataDirectory, "Data0000");
        using (FileStream stream = new(fragment, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // The SVOD layout interleaves a 0x1000 hash table before every
            // group of 0x198 sectors (and a level-1 table periodically). The
            // tables stay zeroed in this fixture; the writer only needs the
            // data-sector offsets that GodVolume maps.
            long length = 0;
            for (int sector = 0; sector < sectors; sector++)
            {
                long offset = MapSector(sector);
                stream.Seek(offset, SeekOrigin.Begin);
                stream.Write(image, sector * Constants.SectorSize, Constants.SectorSize);
                length = Math.Max(length, offset + Constants.SectorSize);
            }

            stream.SetLength(length);
        }

        return headerPath;
    }

    private static long MapSector(long sector)
    {
        const long sectorsPerHashBlock = 0x198;
        const long hashBlockSize = 0x1000;
        long level0Tables = (sector / sectorsPerHashBlock) + 1;
        long level1Tables = (level0Tables / 0xA1C4) + 1;
        return (sector * Constants.SectorSize) + ((level0Tables + level1Tables) * hashBlockSize);
    }

    private static byte[] BuildHeader(
        string titleName,
        uint titleId,
        byte discNumber,
        byte discCount,
        uint dataBlocks)
    {
        byte[] header = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), SignatureLive);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), ContentTypeGod);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(GodHeader.TitleIdOffset), titleId);
        header[GodHeader.DiscNumberOffset] = discNumber;
        header[GodHeader.DiscCountOffset] = discCount;

        byte[] name = Encoding.BigEndianUnicode.GetBytes(titleName);
        name.AsSpan(0, Math.Min(name.Length, GodHeader.TitleNameLength))
            .CopyTo(header.AsSpan(GodHeader.TitleNameOffset));

        header[0x391] = 0; // not enhanced GDF
        header[0x392] = (byte)dataBlocks;
        header[0x393] = (byte)(dataBlocks >> 8);
        header[0x394] = (byte)(dataBlocks >> 16);
        header[0x395] = 0; // starting block
        header[0x396] = 0;
        header[0x397] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x39D), 1); // one data file
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x3A9), 1); // volume type
        return header;
    }
}
