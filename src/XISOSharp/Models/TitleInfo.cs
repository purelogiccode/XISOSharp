using System.Diagnostics.CodeAnalysis;

namespace XISOSharp.Models;

/// <summary>
/// Game metadata derived from the root <c>default.xbe</c> of an original-Xbox
/// image. The executable's certificate is the authoritative source for the
/// title name, title ID, region mask and disc number — the XISO volume
/// descriptor itself carries none of these.
/// </summary>
/// <param name="XbePath">Image-internal path of the executable (<c>/default.xbe</c>).</param>
/// <param name="TitleName">Certificate title name (UTF-16 in the XBE, trimmed).</param>
/// <param name="TitleId">Raw title ID.</param>
/// <param name="TitleIdHex">Title ID formatted as <c>0xXXXXXXXX</c>.</param>
/// <param name="GameRegion">Raw region bitmask (<c>0x01</c> NA, <c>0x02</c> Japan, <c>0x04</c> rest of world).</param>
/// <param name="GameRegionName">Human-readable region summary.</param>
/// <param name="DiskNumber">Disc number of a multi-disc title.</param>
/// <param name="Version">Game version from the certificate (raw DWORD).</param>
/// <param name="Xbe">Full parsed XBE metadata.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public convenience payload: consumers may read the full XbeInfo, the library itself does not.")]
public sealed record TitleInfo(
    string XbePath,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    uint GameRegion,
    string GameRegionName,
    uint DiskNumber,
    uint Version,
    XbeInfo Xbe);
