using System.Diagnostics.CodeAnalysis;
using XISOSharp.Models;
using SharpStfsHeader = StfsSharp.StfsHeader;
using SharpStfsPackageKind = StfsSharp.Models.StfsPackageKind;
using SharpStfsTitleInfo = StfsSharp.Models.StfsTitleInfo;

namespace XISOSharp;

/// <summary>
/// Reads title metadata from the <c>XContent</c> header of a single-file Xbox
/// 360 STFS package (XBLA or XBLIG). Unlike a GOD container, an XBLA/XBLIG
/// package has no <c>.data</c> directory; the package file itself starts with
/// the XContent header. Only the plaintext title fields are read, so no STFS
/// hash tree or block map is needed.
/// </summary>
/// <remarks>
/// This is a compatibility facade over the standalone
/// <see href="https://www.nuget.org/packages/StfsSharp">StfsSharp</see>
/// package. The public XISOSharp model types are preserved and mapped from the
/// StfsSharp models; they will be removed in XISOSharp 2.0.0.
/// </remarks>
public static class StfsHeader
{
    /// <summary>
    /// Returns true when <paramref name="path"/> is a readable single-file STFS
    /// package with an XBLA or XBLIG content type.
    /// </summary>
    public static bool IsStfsPackage(string path) => SharpStfsHeader.IsStfsPackage(path);

    /// <summary>
    /// Reads title metadata from an XBLA/XBLIG STFS package. Returns
    /// <c>false</c> for a GOD/NXE container, another STFS content type, a
    /// truncated/invalid header, or an unreadable file.
    /// </summary>
    /// <param name="path">Path to the single-file STFS package.</param>
    /// <param name="info">Parsed metadata when the method returns <c>true</c>.</param>
    public static bool TryReadTitle(string path, [NotNullWhen(true)] out StfsTitleInfo? info)
    {
        if (!SharpStfsHeader.TryReadTitle(path, out SharpStfsTitleInfo? title))
        {
            info = null;
            return false;
        }

        info = new StfsTitleInfo(
            title.PackagePath,
            title.Kind == SharpStfsPackageKind.Xbla
                ? StfsPackageKind.Xbla
                : StfsPackageKind.Xblig,
            title.TitleName,
            title.TitleId,
            title.TitleIdHex,
            title.DiscNumber,
            title.DiscCount,
            title.ContentType);
        return true;
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryReadTitle"/> that returns
    /// <c>null</c> when the path is not a readable XBLA/XBLIG STFS package.
    /// </summary>
    public static StfsTitleInfo? ReadTitle(string path) =>
        TryReadTitle(path, out StfsTitleInfo? info) ? info : null;
}
