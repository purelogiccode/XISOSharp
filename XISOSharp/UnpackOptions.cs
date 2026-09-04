namespace XISOSharp;

/// <summary>
/// Options controlling unpack/extract/copy-out behavior (TODO #13, xdvdfs #190):
/// an unpack cancelled mid-run restarts by skipping files already on disk
/// instead of rewriting them.
/// </summary>
public sealed class UnpackOptions
{
    /// <summary>
    /// When <c>true</c>, a file already present on disk with the same byte size
    /// is left untouched (logged as <c>skip: &lt;path&gt;</c>) instead of being
    /// overwritten. XISO stores no per-file timestamps, so size is the identity
    /// signal: a same-size file is assumed to be a complete earlier write, and a
    /// missing or short file (a torn write from an interrupted run) is rewritten.
    /// </summary>
    public bool SkipExisting { get; set; }

    /// <summary>
    /// Returns <c>true</c> when <see cref="SkipExisting"/> is set and
    /// <paramref name="destPath"/> already holds a complete file
    /// (<paramref name="fileSize"/> bytes). Unresolvable destinations are never
    /// skipped: the write is attempted and fails with its natural error.
    /// </summary>
    public bool ShouldSkip(string destPath, long fileSize)
    {
        if (!SkipExisting || string.IsNullOrWhiteSpace(destPath) || fileSize < 0)
            return false;

        try
        {
            return File.Exists(destPath) && new FileInfo(destPath).Length == fileSize;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
