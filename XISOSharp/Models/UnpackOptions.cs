namespace XISOSharp.Models;

/// <summary>
/// Options controlling unpack/extract/copy-out behavior (TODO #13, xdvdfs #190;
/// TODO #9, xdvdfs #187): an unpack cancelled mid-run restarts by skipping files
/// already on disk instead of rewriting them, a run over a damaged image or
/// a hostile destination can log per-file failures and finish the rest, and
/// independent files can be written by several workers at once.
/// </summary>
public sealed class UnpackOptions
{
    /// <summary>
    /// Maximum accepted <see cref="MaxDegreeOfParallelism"/> value for the
    /// parallel unpack path (the same 64-worker ceiling the CISO writer and the
    /// CLI's <c>--jobs</c> use).
    /// </summary>
    public const int MaxParallelism = 64;

    private int _maxDegreeOfParallelism = 1;

    /// <summary>
    /// Number of independent files an unpack writes concurrently
    /// (<c>1..<see cref="MaxParallelism"/></c>; default <c>1</c>, the sequential
    /// walk). The directory tree is still walked and created on one thread;
    /// once the walk completes, up to this many workers copy file data from the
    /// image into their destinations. Per-file <c>FileAdded</c> progress is
    /// reported in traversal order, matching a sequential run; byte-level
    /// <c>FileProgress</c> events may interleave between files.
    /// </summary>
    /// <remarks>
    /// Parallel extraction reads the image positionally: a plain <see cref="System.IO.FileStream"/>
    /// is read through <see cref="System.IO.RandomAccess"/> (no shared seek
    /// position), and other sources (CISO/CCI/GOD/split streams) serialize
    /// their reads behind a lock. Destination filesystem calls are serialized
    /// as well, so an <see cref="Interfaces.IFilesystem"/> that is not itself
    /// thread-safe (for example <see cref="MemoryFilesystem"/>) is still safe.
    /// A destination store whose file streams must not be written concurrently
    /// should keep the default of <c>1</c>. Without <see cref="ContinueOnError"/>,
    /// the first failure is reported after the queued copies finish, so later
    /// files may still be extracted (unlike the sequential walk).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is outside <c>1..<see cref="MaxParallelism"/></c>.
    /// </exception>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set
        {
            if (value < 1 || value > MaxParallelism)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"Unpack parallelism must be 1..{MaxParallelism}.");
            }

            _maxDegreeOfParallelism = value;
        }
    }

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
    private List<ExtractFileException> Failures { get; } = [];

    /// <summary>Records a per-file failure for the end-of-run summary.</summary>
    internal void RecordFailure(ExtractFileException failure) => Failures.Add(failure);

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

        IEnumerable<string> lines = Failures.Select(f => $"  {f.Message}");
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
    public bool ShouldSkip(string destPath, long fileSize) => ShouldSkip(destPath, fileSize, LocalFilesystem.Instance);

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
        catch (ArgumentException)
        {
            // Unresolvable spelling (illegal characters): fail open to the
            // write path, which reports the natural error.
            return false;
        }
    }
}
