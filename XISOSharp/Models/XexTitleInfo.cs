namespace XISOSharp.Models;

/// <summary>
/// Title metadata read from the XDBF/XSRC resources of an Xbox 360
/// <c>default.xex</c>. XDBF lives in a plaintext XEX header section, so the
/// title name, publisher, developer, genre, description and thumbnail can be
/// read without decrypting or decompressing the executable image.
/// </summary>
/// <param name="XexPath">Image-internal path of the executable (for example <c>/default.xex</c>).</param>
/// <param name="TitleName">
/// Title name from the XSRC/XSTR resources, or an empty string when the
/// executable carries no readable title metadata.
/// </param>
/// <param name="Publisher">Publisher string from XSRC, when present.</param>
/// <param name="Developer">Developer string from XSRC, when present.</param>
/// <param name="Genre">Genre string from XSRC, when present.</param>
/// <param name="Description">Description/sell text from XSRC, when present.</param>
/// <param name="Thumbnail">
/// Raw thumbnail bytes from the XDBF <c>0x8000</c> image entry, when present.
/// The payload is usually a PNG or JPEG image.
/// </param>
/// <param name="Info">Full XEX2 header metadata.</param>
public sealed record XexTitleInfo(
    string XexPath,
    string TitleName,
    string? Publisher,
    string? Developer,
    string? Genre,
    string? Description,
    byte[]? Thumbnail,
    XexInfo Info)
{
    /// <summary>Raw title ID from the XEX execution info.</summary>
    public uint TitleId => Info.TitleId;

    /// <summary>Title ID formatted as <c>0xXXXXXXXX</c>.</summary>
    public string TitleIdHex => $"0x{Info.TitleId:X8}";

    /// <summary>Whether a thumbnail image was present in the XDBF resource.</summary>
    public bool HasThumbnail => Thumbnail is { Length: > 0 };
}
