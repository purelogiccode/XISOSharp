using XISOSharp.DataStructures;
using XISOSharp.Models;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace XISOSharp;

/// <summary>
/// Callback invoked during extraction/creation to report progress.
/// </summary>
/// <param name="currentValue">Number of bytes processed so far.</param>
/// <param name="finalValue">Total number of bytes to process (may be zero if unknown).</param>
public delegate void ProgressCallback(long currentValue, long finalValue);

/// <summary>
/// Callback invoked for each node during an AVL tree traversal.
/// </summary>
/// <param name="node">The current tree node being visited.</param>
/// <param name="context">Arbitrary context object passed to the traversal.</param>
/// <param name="depth">Current depth within the tree (0 = root).</param>
/// <returns>0 to continue traversal; any non-zero value stops the traversal.</returns>
public delegate int TraversalCallback(AvlNode node, object? context, int depth);

/// <summary>
/// Context used during directory offset calculation for storing the
/// current sector position and directory start offset.
/// Internal implementation detail.
/// </summary>
internal class WdsafpContext
{
    /// <summary>Directory start offset in bytes (sector * 2048).</summary>
    internal long DirStart;

    /// <summary>
    /// Allocator shared with the owning <see cref="DataStructures.OffsetCalcContext"/>,
    /// handing out file-data sectors within the directory.
    /// Required at construction (never left <c>null</c>).
    /// </summary>
    internal required SectorAllocator Allocator;
}

/// <summary>
/// Context passed through the write-tree traversal, bundling the output stream,
/// optional source stream (for rewrite mode), progress callback, and path.
/// Internal implementation detail.
/// </summary>
internal class WriteTreeContext
{
    /// <summary>
    /// The output XISO stream being written to. Any seekable writable
    /// <see cref="Stream"/> (FileStream, MemoryStream, …) — write callbacks must
    /// not downcast it to <see cref="FileStream"/>. Required at construction.
    /// </summary>
    internal required Stream XisoStream;

    /// <summary>
    /// Current path prefix for logging and file construction.
    /// </summary>
    internal string? Path;

    /// <summary>
    /// Source stream for reading original file data in rewrite mode;
    /// <c>null</c> when creating from a file system.
    /// </summary>
    internal Stream? SourceStream;

    /// <summary>Optional byte-progress callback invoked during file writes.</summary>
    internal ProgressCallback? ProgressCallback;

    /// <summary>Optional structured progress channel (create/rewrite events).</summary>
    internal IProgress<ProgressInfo>? StructuredProgress;

    /// <summary>Total expected byte count used for progress reporting.</summary>
    internal long FinalBytes;

    /// <summary>Cancellation token to observe during file writes.</summary>
    internal CancellationToken CancellationToken;

    /// <summary>Byte offset prepended to all physical write positions (skip/prepend support).</summary>
    internal long PrependOffset;

    /// <summary>
    /// Disc lseek offset of <see cref="SourceStream"/> (rewrite mode), carried
    /// explicitly so concurrent rewrites never read a shared global
    /// (BUG-LIB-013). Zero when creating from a file system.
    /// </summary>
    internal long SourceDiscLseek;

    /// <summary>When <c>true</c>, file data is read from <see cref="DataStructures.AvlNode.HostPath"/> instead of the current directory.</summary>
    internal bool IsRemap;
}
