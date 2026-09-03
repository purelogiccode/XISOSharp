using System.Runtime.InteropServices;
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
/// Represents a Windows FILETIME value as two 32-bit unsigned integers.
/// Internal implementation detail used for writing timestamps into XISO headers.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal struct FileTime
{
    /// <summary>Low 32 bits of the FILETIME value.</summary>
#pragma warning disable CS0649 // Field is assigned by external code / spans
    public uint Low;
#pragma warning restore CS0649

    /// <summary>High 32 bits of the FILETIME value.</summary>
#pragma warning disable CS0649
    public uint High;
#pragma warning restore CS0649
}

/// <summary>
/// Context used during directory offset calculation for storing the
/// current sector position and directory start offset.
/// Internal implementation detail.
/// </summary>
internal class WdsafpContext
{
    /// <summary>Directory start offset in bytes (sector * 2048).</summary>
    public long DirStart;

    /// <summary>Current sector counter being assigned.</summary>
    public uint CurrentSector;
}

/// <summary>
/// Context passed through the write-tree traversal, bundling the output stream,
/// optional source stream (for rewrite mode), progress callback, and path.
/// Internal implementation detail.
/// </summary>
internal class WriteTreeContext
{
    /// <summary>The output XISO file stream being written to.</summary>
    public Stream XisoStream = null!;

    /// <summary>
    /// Current path prefix for logging and file construction.
    /// </summary>
    public string? Path;

    /// <summary>
    /// Source stream for reading original file data in rewrite mode;
    /// <c>null</c> when creating from a file system.
    /// </summary>
    public Stream? SourceStream;

    /// <summary>Optional byte-progress callback invoked during file writes.</summary>
    public ProgressCallback? ProgressCallback;

    /// <summary>Optional structured progress channel (create/rewrite events).</summary>
    public IProgress<ProgressInfo>? StructuredProgress;

    /// <summary>Total expected byte count used for progress reporting.</summary>
    public long FinalBytes;

    /// <summary>Cancellation token to observe during file writes.</summary>
    public CancellationToken CancellationToken;

    /// <summary>Byte offset prepended to all physical write positions (skip/prepend support).</summary>
    public long PrependOffset;

    /// <summary>When <c>true</c>, file data is read from <see cref="DataStructures.AvlNode.HostPath"/> instead of the current directory.</summary>
    public bool IsRemap;
}