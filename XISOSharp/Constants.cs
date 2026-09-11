using System.Reflection;

namespace XISOSharp;

/// <summary>
/// All constants for XISO image processing, including magic numbers, sector sizes,
/// offsets, attribute flags, and the optimized tag identifier.
/// Ported from extract-xiso v2.7.1.
/// </summary>
public static class Constants
{
    /// <summary>Magic string identifying a valid XISO header: "MICROSOFT*XBOX*MEDIA".</summary>
    public const string HeaderData = "MICROSOFT*XBOX*MEDIA";

    /// <summary>Length of the header magic string in bytes.</summary>
    public const int HeaderDataLength = 20;

    /// <summary>Offset in bytes from the start of the image where the XISO header resides.</summary>
    public const int HeaderOffset = 0x10000;

    /// <summary>Size of one sector in bytes (2 KB).</summary>
    public const int SectorSize = 2048;

    /// <summary>64 KB alignment modulus for file data in the output image.</summary>
    public const int FileModulus = 0x10000;

    /// <summary>Sector index of the root directory table.</summary>
    public const int RootDirectorySector = 0x108;

    /// <summary>Byte offset where the optimized-tag marker is written.</summary>
    public const int OptimizedTagOffset = 31337;

    /// <summary>Magic string written at the optimized tag offset to mark the image as optimized.</summary>
    public const string OptimizedTag = "in!xiso!2.7.1 (01.11.14)";

    /// <summary>Total length of the optimized tag in bytes.</summary>
    public const int OptimizedTagLength = 24;

    /// <summary>Minimum length of the optimized tag prefix compared during detection.</summary>
    public const int OptimizedTagLengthMin = 7;

    /// <summary>Byte value used for sector padding.</summary>
    public const byte PadByte = 0xFF;

    /// <summary>16-bit word value used for padding directory entries.</summary>
    public const ushort PadShort = 0xFFFF;

    /// <summary>Alternative empty-directory sentinel used by some tools (xdvdfs compatibility).</summary>
    public const ushort EmptyDirectorySentinel = 0x0000;

    /// <summary>
    /// Returns true if <paramref name="value"/> is an empty-directory sentinel
    /// (<c>0xFFFF</c> or <c>0x0000</c>), matching xdvdfs behaviour.
    /// </summary>
    public static bool IsEmptyDirectorySentinel(ushort value) => value == PadShort || value == EmptyDirectorySentinel;

    /// <summary>
    /// Returns true if the 14-byte directory entry header at <paramref name="header"/>
    /// is an empty-directory sentinel (all <c>0xFF</c> or all <c>0x00</c>),
    /// matching <c>xdvdfs-core/src/read.rs:38-39</c>.
    /// </summary>
    public static bool IsEmptyDirectoryHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < FilenameOffset)
            return false;

        bool allFf = true;
        bool allZero = true;
        for (int i = 0; i < FilenameOffset; i++)
        {
            if (header[i] != 0xFF) allFf = false;
            if (header[i] != 0x00) allZero = false;
            if (!allFf && !allZero) return false;
        }

        return allFf || allZero;
    }

    /// <summary>Offset within a directory entry where the filename begins.</summary>
    public const int FilenameOffset = 14;

    /// <summary>Offset within a directory entry where the filename length is stored.</summary>
    public const int FilenameLengthOffset = 13;

    /// <summary>Maximum number of characters permitted in a filename.</summary>
    public const int FilenameMaxChars = 255;

    /// <summary>Size in bytes of the unused/padding region after the FILETIME in the header (0x7C8).</summary>
    public const int UnusedSize = 0x7C8;

    /// <summary>Size of a DWORD in bytes.</summary>
    public const int DwordSize = 4;

    /// <summary>
    /// Maximum directory-table entries visited in a single table walk before the
    /// table is rejected as corrupt (TODO #16, corrupt-TOC hardening). Table
    /// offsets are 16-bit DWORD indexes, so at most 65536 distinct entries can
    /// exist per table; anything beyond that is necessarily a cycle or garbage
    /// chain that would otherwise hang then OOM (extract-xiso #25). In practice
    /// the visited set fires first (a 65537th visit must revisit an offset);
    /// this is the backstop that keeps the bound explicit.
    /// </summary>
    public const int MaxTocEntriesPerTable = 65536;

    /// <summary>
    /// Maximum directory nesting depth (subdirectory descent + intra-table AVL
    /// recursion) before the image is rejected as corrupt (TODO #16). Genuine
    /// Xbox images nest a handful of levels and AVL depth is logarithmic, so
    /// this never fires on valid images; it converts a malicious subdir cycle
    /// (Burnout PAL style) from a <c>StackOverflowException</c> into a named error.
    /// </summary>
    public const int MaxTocDepth = 64;

    /// <summary>Size of a table offset field (2 bytes).</summary>
    public const int TableOffsetSize = 2;

    /// <summary>Size of a sector offset field (4 bytes).</summary>
    public const int SectorOffsetSize = 4;

    /// <summary>Size of the directory table descriptor (4 bytes).</summary>
    public const int DirTableSize = 4;

    /// <summary>Size of a file-size field (4 bytes).</summary>
    public const int FileSizeSize = 4;

    /// <summary>Size of the FILETIME field in bytes.</summary>
    public const int FileTimeSize = 8;

    /// <summary>Size of the attributes byte (1 byte).</summary>
    public const int AttributesSize = 1;

    /// <summary>Size of the filename-length byte (1 byte).</summary>
    public const int FilenameLengthSize = 1;

    /// <summary>8-byte signature pattern searched for during media-enable patching of .xbe files.</summary>
    public static readonly byte[] MediaEnable = [0xE8, 0xCA, 0xFD, 0xFF, 0xFF, 0x85, 0xC0, 0x7D];

    /// <summary>Byte value that replaces the last byte of the media-enable pattern.</summary>
    public const byte MediaEnableByte = 0xEB;

    /// <summary>Length of the media-enable search pattern.</summary>
    public const int MediaEnableLength = 8;

    /// <summary>Index of the byte within the media-enable pattern that gets patched.</summary>
    public const int MediaEnableBytePos = 7;

    /// <summary>Sector lseek offset for global (retail) disc layout.</summary>
    public const uint GlobalLseekOffset = 0x0FD90000;

    /// <summary>Sector lseek offset for XGD2 disc layout (same as GlobalLseekOffset).</summary>
    public const uint Xgd2LseekOffset = GlobalLseekOffset;

    /// <summary>Sector lseek offset for XGD3 disc layout.</summary>
    public const uint Xgd3LseekOffset = 0x02080000;

    /// <summary>Sector lseek offset for XGD1 disc layout.</summary>
    public const uint Xgd1LseekOffset = 0x18300000;

    /// <summary>Sector lseek offset for XGD2-Hybrid disc layout (XGD2 with XGD3-like video).</summary>
    public const uint Xgd2HybridLseekOffset = 0x89D80000;

    /// <summary>Alias for <see cref="Xgd2HybridLseekOffset"/>.</summary>
    public const uint HybridLseekOffset = Xgd2HybridLseekOffset;

    /// <summary>Byte offset where the ECMA-119 primary volume descriptor data area begins.</summary>
    public const int Ecma119DataAreaStart = 0x8000;

    /// <summary>Byte offset for the ECMA-119 volume space size field.</summary>
    public const int Ecma119VolumeSpaceSize = 0x8000 + 80;

    /// <summary>Byte offset for the ECMA-119 volume set size field.</summary>
    public const int Ecma119VolumeSetSize = 0x8000 + 120;

    /// <summary>Byte offset for the ECMA-119 volume set identifier field.</summary>
    public const int Ecma119VolumeSetIdentifier = 0x8000 + 190;

    /// <summary>Byte offset for the ECMA-119 volume creation date field.</summary>
    public const int Ecma119VolumeCreationDate = 0x8000 + 813;

    /// <summary>Size of the read/write buffer used for file copy operations (2 MB).</summary>
    public const int ReadWriteBufferSize = 0x00200000;

    /// <summary>File attribute: read-only.</summary>
    public const byte AttributeRo = 0x01;

    /// <summary>File attribute: hidden.</summary>
    public const byte AttributeHid = 0x02;

    /// <summary>File attribute: system.</summary>
    public const byte AttributeSys = 0x04;

    /// <summary>File attribute: directory.</summary>
    public const byte AttributeDir = 0x10;

    /// <summary>File attribute: archive.</summary>
    public const byte AttributeArc = 0x20;

    /// <summary>File attribute: normal.</summary>
    public const byte AttributeNor = 0x80;

    /// <summary>Mask of reserved attribute bits (3 and 6, i.e. <c>0x08</c> and <c>0x40</c>) that are unused per xdvdfs <c>DirentAttributes</c>.</summary>
    public const byte AttributeReservedMask = 0x48;

    /// <summary>Mask of valid attribute bits (all bits except reserved bits 3 and 6).</summary>
    public const byte AttributeValidMask = 0xB7;

    /// <summary>Masks out reserved attribute bits (3 and 6) from a raw attribute byte, matching xdvdfs behavior.</summary>
    /// <param name="attrs">Raw attribute byte read from disk.</param>
    /// <returns>Attribute byte with reserved bits cleared.</returns>
    public static byte MaskAttributes(byte attrs) => (byte)(attrs & AttributeValidMask);

    /// <summary>
    /// extract-xiso baseline version this port tracks. Kept for source
    /// provenance/parity references; no longer printed by the banner.
    /// </summary>
    public const string ExisoVersion = "2.7.1 (01.11.14)";

    /// <summary>Length of the version string.</summary>
    public const int VersionLength = 16;

    /// <summary>Startup banner text displayed on startup.</summary>
    public static string Banner
    {
        get
        {
            string platform = OperatingSystem.IsWindows() ? "win" :
                OperatingSystem.IsLinux() ? "linux" :
                OperatingSystem.IsMacOS() ? "macos" : "cross-platform";
            return $"XISOSharp v{ProductVersion} for {platform} - https://github.com/purelogiccode/XISOSharp\n";
        }
    }

    /// <summary>
    /// Gets the XISOSharp product version from this assembly's informational
    /// version (MinVer), trimming any <c>+build</c> metadata. Untagged builds
    /// report the prerelease form (e.g. <c>1.0.2-alpha.0.10</c>).
    /// </summary>
    private static string ProductVersion
    {
        get
        {
            try
            {
                string? info = typeof(Constants).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+', StringComparison.Ordinal);
                    return (plus >= 0 ? info[..plus] : info).Trim();
                }

                Version? version = typeof(Constants).Assembly.GetName().Version;
                return version is null ? "unknown" : version.ToString(3);
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>Path separator character used on the target platform.</summary>
    public static readonly char PathChar = Path.DirectorySeparatorChar;

    /// <summary>Path separator as a single-character string.</summary>
    public static readonly string PathCharStr = Path.DirectorySeparatorChar.ToString();

    /// <summary>Default alphabet size for the Boyer-Moore bad-character table.</summary>
    public const int DefaultAlphabetSize = 256;

    /// <summary>
    /// Computes the number of sectors required to hold <paramref name="size"/> bytes,
    /// rounding up to the nearest full sector.
    /// </summary>
    /// <param name="size">Size in bytes.</param>
    /// <returns>Number of 2048-byte sectors required.</returns>
    public static uint NumSectors(uint size) => (size / SectorSize) + (size % SectorSize != 0 ? 1u : 0u);
}
