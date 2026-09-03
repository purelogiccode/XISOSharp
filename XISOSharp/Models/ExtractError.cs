namespace XISOSharp.Models;

/// <summary>Error codes for non-fatal extraction failures.</summary>
public enum ExtractError
{
    /// <summary>XISO image references no files in its directory table.</summary>
    ErrIsoNoFiles = -5003,

    /// <summary>XISO image has already been rewritten (optimized format detected).</summary>
    ErrIsoRewritten = -5002,

    /// <summary>Unexpected end of sector while reading a directory entry chain.</summary>
    ErrEndOfSector = -5001
}
