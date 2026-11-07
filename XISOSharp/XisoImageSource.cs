using System.Buffers.Binary;
using CCISharp;
using XISOSharp.BlockDevice;
using XISOSharp.Interfaces;
using XISOSharp.Models;
using ZArchiveSharp;

namespace XISOSharp;

/// <summary>
/// Single entry point for detecting and opening Xbox image sources.
/// Normalizes plain ISO, CISO/CSO, CCI, GOD/SVOD and their split variants so
/// readers, checksums, repair guards and output naming share one detection
/// table.
/// </summary>
/// <remarks>
/// Detection is extension-led with a magic sniff fallback: a renamed CISO or
/// CCI container (for example a <c>.old</c> rewrite backup) still opens through
/// the decompressed view. GOD headers are content-hash file names (or a
/// <c>.data</c> directory), so they are probed by header content rather than
/// extension. A plain file with an unknown extension is treated as a raw ISO
/// candidate; <see cref="XisoReader.VerifyXiso(IBlockDevice, string, int?)"/>
/// performs the authoritative format check.
/// </remarks>
public static class XisoImageSource
{
    private const uint CisoMagic = 0x4F534943u;
    private const uint CisoHeaderSize = 24;
    private const uint CisoBlockSize = 2048;

    /// <summary>
    /// Shared message for a <c>.zar</c> handed to a sector-based API: an archive
    /// is a name tree, not an image with 2048-byte sectors.
    /// </summary>
    private const string ZarNotSectorImageMessage =
        "ZArchive (.zar) files are not sector images; use XisoZarchive.ListZar/ExtractZar/ConvertZarToIso " +
        "or the CLI's list/extract/checksum/validate/convert verbs.";

    /// <summary>
    /// Attempts to classify <paramref name="path"/> and read its logical length.
    /// Returns <c>false</c> when the path is empty, a known container header is
    /// invalid, or a split part set has no existing parts. A GOD header (or its
    /// <c>.data</c> folder) is classified as <see cref="XisoImageFormat.God"/>;
    /// other plain files classify as <see cref="XisoImageFormat.Iso"/>.
    /// </summary>
    /// <param name="path">Image path (single file, GOD header/`.data` folder or first split part).</param>
    /// <param name="info">Detected source metadata, or <c>null</c> on failure.</param>
    public static bool TryDetect(string? path, out XisoImageSourceInfo? info) =>
        TryDetect(path, FileShare.Read, out info);

    /// <summary>
    /// Attempts to classify <paramref name="path"/> using a caller-chosen share
    /// mode for header probes. Returns <c>false</c> when the path is empty, a
    /// known container header is invalid, or a split part set has no existing
    /// parts. A GOD header (or its <c>.data</c> folder) is classified as
    /// <see cref="XisoImageFormat.God"/>; other plain files classify as
    /// <see cref="XisoImageFormat.Iso"/>.
    /// </summary>
    /// <param name="path">Image path (single file, GOD header/`.data` folder or first split part).</param>
    /// <param name="share">File sharing mode used for header probes.</param>
    /// <param name="info">Detected source metadata, or <c>null</c> on failure.</param>
    public static bool TryDetect(string? path, FileShare share, out XisoImageSourceInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        // GOD headers have no dedicated extension (they are content-hash file
        // names beside a .data directory), so probe before the extension-led
        // paths. The content-type/volume-type check keeps ordinary ISO files
        // with an unrelated .data sibling from being misclassified.
        if (TryDetectGod(path, share, out info)) return true;

        string extension = Path.GetExtension(path);

        if (extension.Equals(".cso", StringComparison.OrdinalIgnoreCase))
        {
            return TryDetectCiso(path, share, out info);
        }

        if (extension.Equals(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return TryDetectCci(path, share, out info);
        }

        if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using ZArchiveReader? reader = ZArchiveReader.TryOpen(path, out ZArchiveOpenFailure failure);
                if (reader is null || failure != ZArchiveOpenFailure.None) return false;
                if (reader.TotalUncompressedSize > long.MaxValue) return false;

                info = new XisoImageSourceInfo(
                    XisoImageFormat.Zar,
                    Path.GetFileName(path),
                    (long)reader.TotalUncompressedSize,
                    [path]);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        if (SplitIsoFile.IsSplitPath(path))
        {
            return TryDetectSplitIso(path, out info);
        }

        if (!File.Exists(path)) return false;

        // Renamed containers (notably `.old` rewrite backups) keep working.
        if (TrySniffMagic(path, share, out XisoImageFormat sniffed))
        {
            if (sniffed == XisoImageFormat.Ciso) return TryDetectCiso(path, share, out info);
            return TryDetectCci(path, share, out info);
        }

        try
        {
            info = new XisoImageSourceInfo(
                XisoImageFormat.Iso,
                Path.GetFileName(path),
                new FileInfo(path).Length,
                [path]);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Returns true when <paramref name="path"/> is a supported image source.</summary>
    /// <param name="path">Image path.</param>
    public static bool IsSupportedImage(string? path) => TryDetect(path, out _);

    /// <summary>Returns true when <paramref name="path"/> is a compressed container (CISO or CCI, single or split).</summary>
    /// <param name="path">Image path.</param>
    public static bool IsCompressedImage(string? path) =>
        TryDetect(path, out XisoImageSourceInfo? info) && info is not null && info.IsCompressed;

    /// <summary>
    /// Returns true when <paramref name="path"/> is a readable GOD/SVOD
    /// container (a header file or its <c>.data</c> directory).
    /// </summary>
    /// <param name="path">GOD header file or matching <c>.data</c> directory.</param>
    public static bool IsGodImage(string? path) =>
        !string.IsNullOrWhiteSpace(path) && GodHeader.TryReadVolume(path, out _);

    /// <summary>
    /// Returns true when <paramref name="path"/> has a CISO or CCI extension,
    /// without opening the file. Used by in-place guards that must refuse a
    /// compressed container even when the file is missing or corrupt.
    /// </summary>
    /// <param name="path">Image path.</param>
    public static bool IsCompressedContainerPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string extension = Path.GetExtension(path);
        return extension.Equals(".cso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(CciFormat.Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens an image for reading, decompressing CISO/CCI containers, mapping
    /// GOD/SVOD fragments and stitching split parts transparently. The caller
    /// owns the returned stream.
    /// </summary>
    /// <param name="path">Image path (single file, GOD header/`.data` folder or first split part).</param>
    /// <param name="share">File sharing mode for the underlying file(s).</param>
    /// <exception cref="FileNotFoundException">The image or a split part does not exist.</exception>
    /// <exception cref="InvalidDataException">A container header or index is invalid.</exception>
    /// <exception cref="NotSupportedException">The path is a ZArchive (not a sector image).</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public static Stream OpenRead(string path, FileShare share = FileShare.Read)
    {
        XisoImageSourceInfo info = DetectOrThrow(path, share);

        return info.Format switch
        {
            XisoImageFormat.Iso => new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = share,
                    BufferSize = 65536,
                    Options = FileOptions.SequentialScan,
                }),
            XisoImageFormat.SplitIso => new SplitIsoStream(info.Parts, share),
            XisoImageFormat.Ciso or XisoImageFormat.SplitCiso =>
                new BlockDeviceStream(new CisoBlockDevice(path, share)),
            XisoImageFormat.Cci or XisoImageFormat.SplitCci =>
                new BlockDeviceStream(new CciBlockDevice(path, share)),
            XisoImageFormat.God => new BlockDeviceStream(new GodBlockDevice(path, share)),
            XisoImageFormat.Zar => throw new NotSupportedException(ZarNotSectorImageMessage),
            _ => throw new InvalidDataException($"Unsupported image format for '{path}'."),
        };
    }

    /// <summary>
    /// Opens an image as an <see cref="IBlockDevice"/>, decompressing CISO/CCI
    /// containers, mapping GOD/SVOD fragments and stitching split parts
    /// transparently. The caller owns the returned device.
    /// </summary>
    /// <param name="path">Image path (single file, GOD header/`.data` folder or first split part).</param>
    /// <param name="share">File sharing mode for the underlying file(s).</param>
    /// <exception cref="FileNotFoundException">The image or a split part does not exist.</exception>
    /// <exception cref="InvalidDataException">A container header or index is invalid.</exception>
    /// <exception cref="NotSupportedException">The path is a ZArchive (not a sector image).</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public static IBlockDevice OpenBlockDevice(string path, FileShare share = FileShare.Read)
    {
        XisoImageSourceInfo info = DetectOrThrow(path, share);

        return info.Format switch
        {
            XisoImageFormat.Iso => new FileBlockDevice(path, FileMode.Open, FileAccess.Read, share),
            XisoImageFormat.SplitIso => new StreamBlockDevice(new SplitIsoStream(info.Parts, share)),
            XisoImageFormat.Ciso or XisoImageFormat.SplitCiso => new CisoBlockDevice(path, share),
            XisoImageFormat.Cci or XisoImageFormat.SplitCci => new CciBlockDevice(path, share),
            XisoImageFormat.God => new GodBlockDevice(path, share),
            XisoImageFormat.Zar => throw new NotSupportedException(ZarNotSectorImageMessage),
            _ => throw new InvalidDataException($"Unsupported image format for '{path}'."),
        };
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> ends with a recognized image
    /// container extension (<c>.iso</c>, <c>.xiso</c>, <c>.img</c>, <c>.cso</c>,
    /// <c>.cci</c>).
    /// </summary>
    /// <param name="name">File name or path.</param>
    public static bool HasImageSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        string extension = Path.GetExtension(name);
        return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xiso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".img", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(CciFormat.Extension, StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips a recognized image suffix and a trailing split-part marker for
    /// output naming: <c>game.iso</c> → <c>game</c>, <c>game.1.cso</c> →
    /// <c>game</c>, <c>game.1.cci</c> → <c>game</c>. CCI accepts any numbered
    /// part as an entry point (CCISharp resolves the whole set from <c>.1</c>),
    /// so <c>game.2.cci</c> and <c>game.10.cci</c> → <c>game</c> as well. Plain
    /// ISO and CISO sets only expose <c>.1</c>, and ZArchive names such as
    /// <c>archive.1.zar</c> keep their number. Names without a recognized
    /// suffix are returned unchanged.
    /// </summary>
    /// <param name="name">File name or path.</param>
    public static string StripImageSuffix(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!HasImageSuffix(name)) return name;

        string extension = Path.GetExtension(name);
        string stem = name[..^extension.Length];

        if (extension.Equals(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            string stripped = CciNaming.StripPartSuffix(name);
            return stripped[..^extension.Length];
        }

        if (stem.Length < 2 || stem[^2] != '.') return stem;

        char part = stem[^1];
        bool isPart;
        if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cso", StringComparison.OrdinalIgnoreCase))
        {
            isPart = part == '1';
        }
        else
        {
            isPart = false;
        }

        return isPart ? stem[..^2] : stem;
    }

    private static XisoImageSourceInfo DetectOrThrow(string path, FileShare share)
    {
        if (TryDetect(path, share, out XisoImageSourceInfo? info) && info is not null)
        {
            return info;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"Image not found: {path}", path);
        }

        throw new InvalidDataException($"Unsupported or invalid Xbox image: {path}");
    }

    private static bool TryDetectGod(string path, FileShare share, out XisoImageSourceInfo? info)
    {
        info = null;
        if (!GodHeader.TryReadVolume(path, share, out GodVolumeInfo? volume))
            return false;

        string displayName = GodHeader.ReadTitle(volume.HeaderPath)?.TitleName
                             ?? Path.GetFileName(volume.HeaderPath);
        info = new XisoImageSourceInfo(
            XisoImageFormat.God,
            displayName,
            volume.Length,
            volume.Parts);
        return true;
    }

    private static bool TryDetectSplitIso(string path, out XisoImageSourceInfo? info)
    {
        info = null;
        List<string> parts = SplitIsoFile.DiscoverPartPaths(path);
        if (parts.Count == 0) return false;

        try
        {
            info = new XisoImageSourceInfo(
                XisoImageFormat.SplitIso,
                Path.GetFileName(path),
                SplitIsoFile.GetCombinedLength(parts),
                parts);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryDetectCiso(string path, FileShare share, out XisoImageSourceInfo? info)
    {
        info = null;
        bool split = CisoSplitFile.IsSplitPath(path);
        List<string> parts = CisoSplitFile.DiscoverPartPaths(path);
        if (parts.Count == 0) return false;

        try
        {
            // The first part always carries the CISO header, including the
            // total uncompressed size for a split set.
            if (!TryReadCisoHeaderLength(parts[0], share, out long length) || length <= 0) return false;

            info = new XisoImageSourceInfo(
                split ? XisoImageFormat.SplitCiso : XisoImageFormat.Ciso,
                Path.GetFileName(path),
                length,
                parts);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDetectCci(string path, FileShare share, out XisoImageSourceInfo? info)
    {
        info = null;
        bool split = CciNaming.IsSplitPath(path);
        List<string> parts = CciNaming.DiscoverPartPaths(path);
        if (parts.Count == 0) return false;

        if (!CciNaming.TryGetLength(parts, share, out long length)) return false;

        info = new XisoImageSourceInfo(
            split ? XisoImageFormat.SplitCci : XisoImageFormat.Cci,
            Path.GetFileName(path),
            length,
            parts);
        return true;
    }

    private static bool TryReadCisoHeaderLength(string path, FileShare share, out long length)
    {
        length = 0;
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, share, 64);
        Span<byte> header = stackalloc byte[(int)CisoHeaderSize];
        int read = 0;
        while (read < header.Length)
        {
            int n = fs.Read(header[read..]);
            if (n == 0) return false;
            read += n;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        ulong claimed = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        byte version = header[20];

        if (magic != CisoMagic ||
            headerSize != CisoHeaderSize ||
            blockSize != CisoBlockSize ||
            version is not (CisoWriter.VersionDeflate or CisoWriter.VersionLz4) ||
            claimed is 0 or > long.MaxValue)
        {
            return false;
        }

        length = (long)claimed;
        return true;
    }

    private static bool TrySniffMagic(string path, FileShare share, out XisoImageFormat format)
    {
        format = XisoImageFormat.Iso;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, share, 64);
            Span<byte> header = stackalloc byte[8];
            int read = 0;
            while (read < header.Length)
            {
                int n = fs.Read(header[read..]);
                if (n == 0) return false;
                read += n;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);

            if (magic == CisoMagic && size == CisoHeaderSize)
            {
                format = XisoImageFormat.Ciso;
                return true;
            }

            if (magic == CciFormat.Magic && size == CciFormat.HeaderSize)
            {
                format = XisoImageFormat.Cci;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
