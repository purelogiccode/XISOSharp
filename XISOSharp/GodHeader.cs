using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Reads the <c>XContent</c> header of a Games-on-Demand (GOD/SVOD) container.
/// A GOD container is a header file named by content hash next to a matching
/// <c>&lt;header&gt;.data</c> directory. The header is plaintext and carries the
/// title name, title ID and disc number/count, so this parser never decodes a
/// data sector and never needs the STFS/SVOD block map.
/// </summary>
/// <remarks>
/// Layout facts (big-endian, matching the Xbox 360 XContent structure):
/// signature type at <c>0x000</c> (CON/LIVE/PIRS), content type at
/// <c>0x344</c> (0x7000 GOD or 0x4000 NXE), title ID at <c>0x360</c>, disc
/// number/count at <c>0x366</c>/<c>0x367</c>, and the 0x80-byte UTF-16BE title
/// name at <c>0x411</c>. Only these fixed fields are read. Single-file XBLA and
/// XBLIG packages use the same header layout; see <see cref="StfsHeader"/>.
/// </remarks>
public static class GodHeader
{
    /// <summary>Offset of the big-endian title ID in the header.</summary>
    public const int TitleIdOffset = 0x360;

    /// <summary>Offset of the disc-number byte in the header.</summary>
    public const int DiscNumberOffset = 0x366;

    /// <summary>Offset of the disc-count byte in the header.</summary>
    public const int DiscCountOffset = 0x367;

    /// <summary>Offset of the UTF-16BE title-name field in the header.</summary>
    public const int TitleNameOffset = 0x411;

    /// <summary>Length in bytes of the UTF-16BE title-name field.</summary>
    public const int TitleNameLength = 0x80;

    /// <summary>
    /// Minimum header length needed to read the title-name field
    /// (<see cref="TitleNameOffset"/> + <see cref="TitleNameLength"/>).
    /// </summary>
    public const int MinimumHeaderLength = TitleNameOffset + TitleNameLength;

    /// <summary>
    /// Returns true when <paramref name="path"/> is a GOD header file or its
    /// <c>.data</c> directory: the header exists, the sibling
    /// <c>&lt;header&gt;.data</c> directory exists, and the header carries a
    /// recognized XContent signature/content type.
    /// </summary>
    public static bool IsGodContainer(string path) => TryReadTitle(path, out _);

    /// <summary>
    /// Reads title metadata from a GOD header path or its <c>.data</c>
    /// directory. Returns <c>false</c> for a path that is not a GOD container,
    /// a truncated/invalid header, or an unreadable file.
    /// </summary>
    /// <param name="path">GOD header file or the matching <c>.data</c> directory.</param>
    /// <param name="info">Parsed metadata when the method returns <c>true</c>.</param>
    public static bool TryReadTitle(string path, [NotNullWhen(true)] out GodTitleInfo? info)
    {
        info = null;
        if (!TryResolveHeaderPath(path, out string headerPath) ||
            !XContentHeaderReader.TryReadHeader(headerPath, out byte[] header))
        {
            return false;
        }

        uint contentType = XContentHeaderReader.ReadContentType(header);
        if (contentType is not (XContentHeaderReader.ContentTypeGod or XContentHeaderReader.ContentTypeNxe))
            return false;

        uint titleId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(TitleIdOffset));
        byte discNumber = header[DiscNumberOffset];
        byte discCount = header[DiscCountOffset];

        string titleName = XContentHeaderReader.SanitizeTitleName(
            XContentHeaderReader.DecodeTitleName(header.AsSpan(TitleNameOffset, TitleNameLength)));
        if (titleName.Length == 0)
            titleName = titleId.ToString("X8", CultureInfo.InvariantCulture);

        info = new GodTitleInfo(
            headerPath,
            titleName,
            titleId,
            $"0x{titleId:X8}",
            discNumber,
            discCount);
        return true;
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryReadTitle"/> that returns
    /// <c>null</c> when the path is not a readable GOD container.
    /// </summary>
    public static GodTitleInfo? ReadTitle(string path) =>
        TryReadTitle(path, out GodTitleInfo? info) ? info : null;

    private static bool TryResolveHeaderPath(string path, out string headerPath)
    {
        headerPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(trimmed))
        {
            if (!trimmed.EndsWith(".data", StringComparison.OrdinalIgnoreCase))
                return false;

            string candidate = trimmed[..^5];
            if (!File.Exists(candidate))
                return false;

            headerPath = candidate;
            return true;
        }

        if (!File.Exists(trimmed))
            return false;

        headerPath = trimmed;
        return Directory.Exists(trimmed + ".data");
    }
}
