namespace XISOSharp.Models;

/// <summary>Identifies an image carried by an original-Xbox XBE executable.</summary>
public enum XbeImageKind
{
    /// <summary>The Microsoft logo bitmap addressed by the XBE image header.</summary>
    Logo = 0,

    /// <summary>The <c>$$XTIMAGE</c> title image section.</summary>
    Title = 1,

    /// <summary>The <c>$$XSIMAGE</c> save image section.</summary>
    Save = 2,
}

/// <summary>
/// An image extracted from an original-Xbox XBE executable. <see cref="Raw"/>
/// always contains the on-disk payload; <see cref="Png"/> and <see cref="Dds"/>
/// are populated when the payload could be decoded or wrapped losslessly.
/// </summary>
/// <param name="Kind">Image kind.</param>
/// <param name="SectionName">Section name (<c>$$XTIMAGE</c>/<c>$$XSIMAGE</c>), or <c>logo</c>.</param>
/// <param name="Width">Decoded image width in pixels (0 when unknown).</param>
/// <param name="Height">Decoded image height in pixels (0 when unknown).</param>
/// <param name="PixelFormat">Detected source pixel format (for example <c>DXT1</c>, <c>BGR24</c> or <c>XPR0/DXT5</c>).</param>
/// <param name="Raw">Raw on-disk payload.</param>
/// <param name="Png">PNG encoding of the decoded image, or <c>null</c> when unsupported.</param>
/// <param name="Dds">DDS encoding of the image (passthrough or generated), or <c>null</c> when unsupported.</param>
public sealed record XbeImage(
    XbeImageKind Kind,
    string SectionName,
    int Width,
    int Height,
    string PixelFormat,
    byte[] Raw,
    byte[]? Png,
    byte[]? Dds);
