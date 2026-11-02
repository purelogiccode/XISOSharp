namespace XISOSharp.Models;

/// <summary>
/// Identifies the executable that supplied <see cref="TitleInfo"/> metadata.
/// </summary>
public enum TitleKind
{
    /// <summary>Metadata came from an original-Xbox XBEH executable.</summary>
    Xbe = 0,

    /// <summary>Metadata came from an Xbox 360 XEX2 executable.</summary>
    Xex = 1,
}
