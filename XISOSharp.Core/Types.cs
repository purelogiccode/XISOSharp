using System.Runtime.InteropServices;

using XISOSharp.DataStructures;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace XISOSharp;

/// <summary>Result codes returned by AVL tree insertion.</summary>
public enum AvlResult
{
    /// <summary>Operation completed successfully without requiring rebalancing.</summary>
    NoErr,
    /// <summary>An error occurred during the operation.</summary>
    AvlError,
    /// <summary>Operation completed and the tree was rebalanced.</summary>
    AvlBalanced
}

/// <summary>Traversal order when walking an AVL tree.</summary>
public enum AvlTraversalMethod
{
    /// <summary>Visit the current node before its children (pre-order traversal).</summary>
    Prefix,
    /// <summary>Visit the left child, then the current node, then the right child (in-order traversal).</summary>
    Infix,
    /// <summary>Visit children before the current node (post-order traversal).</summary>
    Postfix
}

/// <summary>Operating mode for XISO image processing.</summary>
public enum ExtractMode
{
    /// <summary>Build the AVL tree directory structure without writing an output file.</summary>
    GenerateAvl,
    /// <summary>Extract files from the XISO image to disk.</summary>
    Extract,
    /// <summary>List the contents of the XISO image.</summary>
    List,
    /// <summary>Rewrite the XISO image with an optimized AVL directory structure.</summary>
    Rewrite,
    /// <summary>Recursively list all files with sizes in a tree format.</summary>
    Tree,
    /// <summary>Deep-audit the XISO image: validate header, walk tree, check sector bounds, detect cycles.</summary>
    Verify
}

/// <summary>Error codes for non-fatal extraction failures.</summary>
public enum ExtractError
{
    /// <summary>Unexpected end of sector while reading a directory entry chain.</summary>
    ErrEndOfSector = -5001,
    /// <summary>XISO image has already been rewritten (optimized format detected).</summary>
    ErrIsoRewritten = -5002,
    /// <summary>XISO image references no files in its directory table.</summary>
    ErrIsoNoFiles = -5003
}

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
/// Describes a source directory and optional output name for creating an XISO image.
/// Entries can be chained via <see cref="Next"/> for batch creation.
/// </summary>
public class CreateList
{
    /// <summary>Source directory path whose contents will be packed into the XISO.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional output filename or path for the resulting ISO.
    /// When <c>null</c> the directory name is used.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Next entry in a linked list of creation tasks, or <c>null</c>.</summary>
    public CreateList? Next { get; set; }
}

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

    /// <summary>Optional progress callback invoked during file writes.</summary>
    public ProgressCallback? Progress;

    /// <summary>Total expected byte count used for progress reporting.</summary>
    public long FinalBytes;

    /// <summary>Cancellation token to observe during file writes.</summary>
    public CancellationToken CancellationToken;
}
