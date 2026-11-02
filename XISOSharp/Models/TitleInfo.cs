using System.Diagnostics.CodeAnalysis;

namespace XISOSharp.Models;

/// <summary>
/// Game metadata derived from the root executable of an image. For an
/// original-Xbox image the XBE certificate is authoritative; for an Xbox 360
/// image the XEX2 execution info plus the plaintext XDBF/XSRC resources supply
/// the title name, title ID, region, disc and version.
/// </summary>
/// <param name="XbePath">
/// Image-internal path of the executable (<c>/default.xbe</c>,
/// <c>/default.xex</c> or <c>/default_mp.xbe</c>).
/// </param>
/// <param name="TitleName">Title name (XBE certificate or XDBF/XSRC title string).</param>
/// <param name="TitleId">Raw title ID.</param>
/// <param name="TitleIdHex">Title ID formatted as <c>0xXXXXXXXX</c>.</param>
/// <param name="GameRegion">Raw region bitmask.</param>
/// <param name="GameRegionName">Human-readable region summary.</param>
/// <param name="DiskNumber">Disc number of a multi-disc title.</param>
/// <param name="Version">Game version (raw DWORD).</param>
/// <param name="Xbe">
/// Full parsed XBE metadata for <see cref="TitleKind.Xbe"/> titles; <c>null</c>
/// for <see cref="TitleKind.Xex"/> titles (see <see cref="Xex"/>).
/// </param>
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
    XbeInfo? Xbe)
{
    /// <summary>Which executable supplied this metadata.</summary>
    public TitleKind Kind { get; init; } = TitleKind.Xbe;

    /// <summary>
    /// Xbox 360 title metadata for <see cref="TitleKind.Xex"/> titles;
    /// <c>null</c> for original-Xbox titles.
    /// </summary>
    public XexTitleInfo? Xex { get; init; }
}
