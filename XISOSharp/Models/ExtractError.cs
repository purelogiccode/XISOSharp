namespace XISOSharp.Models;

/// <summary>Error codes for non-fatal extraction failures.</summary>
public enum ExtractError
{
    /// <summary>XISO image references no files in its directory table.</summary>
    ErrIsoNoFiles = -5003,

    /// <summary>XISO image has already been rewritten (optimized format detected).</summary>
    ErrIsoRewritten = -5002,

    /// <summary>Unexpected end of sector while reading a directory entry chain.</summary>
    ErrEndOfSector = -5001,

    /// <summary>
    /// File data ends before the reported size (truncated image or entry
    /// pointing past end of image). Carried by <c>ExtractFileException</c>.
    /// </summary>
    ErrFileTruncated = -5004,

    /// <summary>
    /// Destination file or directory could not be created or written.
    /// Carried by <c>ExtractFileException</c> with the OS error as inner.
    /// </summary>
    ErrFileWrite = -5005,

    /// <summary>
    /// One or more files failed under <c>UnpackOptions.ContinueOnError</c>;
    /// the message lists every failure. The first failure is the inner exception.
    /// </summary>
    ErrExtractFailed = -5006
}