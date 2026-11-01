using System.Diagnostics.CodeAnalysis;

namespace XISOSharp;

/// <summary>
/// Describes a detected Xbox image source and its on-disk parts.
/// </summary>
/// <param name="Format">Container format.</param>
/// <param name="DisplayName">File name used in logs and error messages (the first part for split sets).</param>
/// <param name="Length">Logical uncompressed image length in bytes.</param>
/// <param name="Parts">Part paths in image order; a single element for non-split images.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public detection payload: consumers read DisplayName, the library itself does not.")]
public sealed record XisoImageSourceInfo(
    XisoImageFormat Format,
    string DisplayName,
    long Length,
    IReadOnlyList<string> Parts)
{
    /// <summary>True for split sets (<c>SplitIso</c>, <c>SplitCiso</c>, <c>SplitCci</c>).</summary>
    public bool IsSplit => Format is XisoImageFormat.SplitIso or XisoImageFormat.SplitCiso or XisoImageFormat.SplitCci;

    /// <summary>True for compressed containers (<c>Ciso</c>, <c>Cci</c> and their split variants).</summary>
    public bool IsCompressed => Format is XisoImageFormat.Ciso or XisoImageFormat.Cci
        or XisoImageFormat.SplitCiso or XisoImageFormat.SplitCci;

    /// <summary>Path of the first part, or an empty string when <see cref="Parts"/> is empty.</summary>
    public string FirstPart => Parts.Count > 0 ? Parts[0] : string.Empty;
}
