namespace XISOSharp;

/// <summary>
/// Container format detected for an Xbox image source.
/// </summary>
public enum XisoImageFormat
{
    /// <summary>A plain, uncompressed ISO/XISO image.</summary>
    Iso,

    /// <summary>A single-file CISO/CSO compressed image.</summary>
    Ciso,

    /// <summary>A single-file CCI ("CCIM") compressed image.</summary>
    Cci,

    /// <summary>A split plain-ISO set (<c>game.1.iso</c>, <c>game.2.iso</c>, …).</summary>
    SplitIso,

    /// <summary>A split CISO set (<c>game.1.cso</c>, <c>game.2.cso</c>, …).</summary>
    SplitCiso,

    /// <summary>A split CCI set (<c>game.1.cci</c> … <c>game.9.cci</c>).</summary>
    SplitCci,

    /// <summary>A ZArchive (<c>.zar</c>) name-tree container (not a sector image).</summary>
    Zar,
}
