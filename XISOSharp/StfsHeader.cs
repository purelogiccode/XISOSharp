using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Reads title metadata from the <c>XContent</c> header of a single-file Xbox
/// 360 STFS package (XBLA or XBLIG). Unlike a GOD container, an XBLA/XBLIG
/// package has no <c>.data</c> directory; the package file itself starts with
/// the XContent header. Only the plaintext title fields are read, so no STFS
/// hash tree or block map is needed.
/// </summary>
/// <remarks>
/// Supported content types: XBLA <c>0x000D0000</c> and XBLIG
/// <c>0x00000002</c> (Community Game <c>0x02000000</c> is accepted as XBLIG).
/// GOD/NXE containers are handled by <see cref="GodHeader"/>.
/// The title name is at <c>0x411</c> (UTF-16BE, 0x80 bytes), title ID at
/// <c>0x360</c>, and disc number/count at <c>0x366</c>/<c>0x367</c>.
/// </remarks>
public static class StfsHeader
{
    /// <summary>
    /// Returns true when <paramref name="path"/> is a readable single-file STFS
    /// package with an XBLA or XBLIG content type.
    /// </summary>
    public static bool IsStfsPackage(string path)
    {
        if (!XContentHeaderReader.TryReadHeader(path, out byte[] header))
            return false;

        if (!HasStfsVolumeType(header))
            return false;

        uint contentType = XContentHeaderReader.ReadContentType(header);
        return contentType is XContentHeaderReader.ContentTypeXbla or
            XContentHeaderReader.ContentTypeXblig or
            XContentHeaderReader.ContentTypeCommunityGame;
    }

    /// <summary>
    /// Reads title metadata from an XBLA/XBLIG STFS package. Returns
    /// <c>false</c> for a GOD/NXE container, another STFS content type, a
    /// truncated/invalid header, or an unreadable file.
    /// </summary>
    /// <param name="path">Path to the single-file STFS package.</param>
    /// <param name="info">Parsed metadata when the method returns <c>true</c>.</param>
    public static bool TryReadTitle(string path, [NotNullWhen(true)] out StfsTitleInfo? info)
    {
        info = null;
        if (!XContentHeaderReader.TryReadHeader(path, out byte[] header))
            return false;

        if (!HasStfsVolumeType(header))
            return false;

        uint contentType = XContentHeaderReader.ReadContentType(header);
        StfsPackageKind kind;
        if (contentType == XContentHeaderReader.ContentTypeXbla)
        {
            kind = StfsPackageKind.Xbla;
        }
        else if (contentType is XContentHeaderReader.ContentTypeXblig or
                 XContentHeaderReader.ContentTypeCommunityGame)
        {
            kind = StfsPackageKind.Xblig;
        }
        else
        {
            return false;
        }

        uint titleId = BinaryPrimitives.ReadUInt32BigEndian(
            header.AsSpan(XContentHeaderReader.TitleIdOffset));
        byte discNumber = header[XContentHeaderReader.DiscNumberOffset];
        byte discCount = header[XContentHeaderReader.DiscCountOffset];

        string titleName = XContentHeaderReader.SanitizeTitleName(
            XContentHeaderReader.DecodeTitleName(
                header.AsSpan(XContentHeaderReader.TitleNameOffset, XContentHeaderReader.TitleNameLength)));
        if (titleName.Length == 0)
            titleName = titleId.ToString("X8", CultureInfo.InvariantCulture);

        info = new StfsTitleInfo(
            Path.GetFullPath(path),
            kind,
            titleName,
            titleId,
            $"0x{titleId:X8}",
            discNumber,
            discCount,
            contentType);
        return true;
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryReadTitle"/> that returns
    /// <c>null</c> when the path is not a readable XBLA/XBLIG STFS package.
    /// </summary>
    public static StfsTitleInfo? ReadTitle(string path) =>
        TryReadTitle(path, out StfsTitleInfo? info) ? info : null;

    private static bool HasStfsVolumeType(ReadOnlySpan<byte> header) =>
        BinaryPrimitives.ReadUInt32BigEndian(header[GodHeader.VolumeTypeOffset..]) ==
        GodHeader.VolumeTypeStfs;
}
