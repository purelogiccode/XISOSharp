namespace XISOSharp.Models;

/// <summary>Kind of single-file Xbox 360 STFS package.</summary>
public enum StfsPackageKind
{
    /// <summary>Xbox Live Arcade package (content type <c>0x000D0000</c>).</summary>
    Xbla,

    /// <summary>Xbox Live Indie Games package (content type <c>0x00000002</c>).</summary>
    Xblig,
}

/// <summary>
/// Title metadata read from the <c>XContent</c> header of a single-file Xbox
/// 360 STFS package (XBLA or XBLIG). Unlike a GOD container, these packages
/// have no <c>.data</c> directory; the package file itself starts with the
/// XContent header.
/// </summary>
/// <param name="PackagePath">Absolute path of the STFS package file.</param>
/// <param name="Kind">Package kind (XBLA or XBLIG).</param>
/// <param name="TitleName">
/// FATX-safe title name decoded from the header (non-ASCII and control
/// characters removed, path-unsafe characters replaced with <c>_</c>). Falls
/// back to the hex title ID when the stored name is empty.
/// </param>
/// <param name="TitleId">Raw title ID (big-endian in the header).</param>
/// <param name="TitleIdHex">Title ID formatted as <c>0xXXXXXXXX</c>.</param>
/// <param name="DiscNumber">Disc number of a multi-disc title.</param>
/// <param name="DiscCount">Number of discs in the set.</param>
/// <param name="ContentType">Raw XContent content type.</param>
public sealed record StfsTitleInfo(
    string PackagePath,
    StfsPackageKind Kind,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    byte DiscNumber,
    byte DiscCount,
    uint ContentType);
