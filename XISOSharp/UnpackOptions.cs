using XISOSharp.Models;

namespace XISOSharp;

/// <summary>
/// Options controlling unpack/extract/copy-out behavior (TODO #13, xdvdfs #190;
/// TODO #9, xdvdfs #187): an unpack cancelled mid-run restarts by skipping files
/// already on disk instead of rewriting them, and a run over a damaged image or
/// a hostile destination can log per-file failures and finish the rest.
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
    /// When <c>true</c>, a per-file extraction failure (uncreatable destination,
    /// truncated data, failed write) is recorded in <see cref="Failures"/>,
    /// reported to stderr, and extraction continues with the next entry instead
    /// of aborting the run (TODO #9, xdvdfs #187: the web unpacker logs
    /// <c>Failed to create file X</c> per file rather than dying silently).
    /// A directory that cannot be created skips its whole subtree. When the run
    /// ends with any recorded failure, <see cref="ThrowIfFailed"/> throws a
    /// summary, so the exit code still signals failure. Corrupt directory
    /// tables (TODO #16: cycles, out-of-table offsets, absurd entry counts)
    /// skip the bad subtree and are recorded like any other per-file failure:
    /// every sibling seek is absolute, so the stream position stays recoverable.
    /// </summary>
    public bool ContinueOnError { get; set; }

    /// <summary>
    /// Per-file failures recorded during a <see cref="ContinueOnError"/> run,
    /// in encounter order. Empty unless <see cref="ContinueOnError"/> is set.
    /// </summary>
    internal List<ExtractFileException> Failures { get; } = [];

    /// <summary>Records a per-file failure for the end-of-run summary.</summary>
    internal void RecordFailure(ExtractFileException failure)
    {
        Failures.Add(failure);
    }

    /// <summary>
    /// Throws an <see cref="ExtractErrorException"/> with code
    /// <see cref="ExtractError.ErrExtractFailed"/> listing every recorded
    /// failure (the xdvdfs <c>Failed to unpack image</c> wrapper) when
    /// <see cref="Failures"/> is non-empty; otherwise a no-op.
    /// </summary>
    /// <param name="imageName">Display name of the image, for the summary line.</param>
    internal void ThrowIfFailed(string imageName)
    {
        if (Failures.Count == 0)
            return;

        var lines = Failures.Select(f => $"  {f.Message}");
        throw new ExtractErrorException(ExtractError.ErrExtractFailed,
            $"Failed to unpack image \"{imageName}\": {Failures.Count} file(s) failed:\n" +
            string.Join("\n", lines),
            Failures[0]);
    }

    /// <summary>
    /// Returns <c>true</c> when <see cref="SkipExisting"/> is set and
    /// <paramref name="destPath"/> already holds a complete file
    /// (<paramref name="fileSize"/> bytes). Unresolvable destinations are never
    /// skipped: the write is attempted and fails with its natural error.
    /// </summary>
    public bool ShouldSkip(string destPath, long fileSize)
    {
        return ShouldSkip(destPath, fileSize, LocalFilesystem.Instance);
    }

    /// <summary>
    /// Filesystem-aware <see cref="ShouldSkip(string, long)"/>: probes
    /// <paramref name="filesystem"/> instead of the local disk, so resume works
    /// for every destination (<see cref="LocalFilesystem"/>,
    /// <see cref="MemoryFilesystem"/>, custom stores; TODO #7).
    /// </summary>
    /// <param name="destPath">Destination path in the filesystem's own path form.</param>
    /// <param name="fileSize">Byte size the image reports for the file.</param>
    /// <param name="filesystem">Destination filesystem to probe.</param>
    public bool ShouldSkip(string destPath, long fileSize, Interfaces.IFilesystem filesystem)
    {
        if (!SkipExisting || string.IsNullOrWhiteSpace(destPath) || fileSize < 0)
            return false;

        try
        {
            return filesystem.FileExists(destPath) && filesystem.FileLength(destPath) == fileSize;
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