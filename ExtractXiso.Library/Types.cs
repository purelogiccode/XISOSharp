using ExtractXiso.DataStructures;

namespace ExtractXiso;

/// <summary>Result codes returned by AVL tree insertion.</summary>
public enum AvlResult { NoErr, AvlError, AvlBalanced }

/// <summary>Traversal order when walking an AVL tree.</summary>
public enum AvlTraversalMethod { Prefix, Infix, Postfix }

/// <summary>Operating mode for XISO image processing.</summary>
public enum ExtractMode { GenerateAvl, Extract, List, Rewrite }

/// <summary>Error codes for non-fatal extraction failures.</summary>
public enum ExtractError { ErrEndOfSector = -5001, ErrIsoRewritten = -5002, ErrIsoNoFiles = -5003 }

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
/// </summary>
public class CreateList
{
    /// <summary>Source directory path whose contents will be packed into the XISO.</summary>
    public string Path = "";

    /// <summary>
    /// Optional output filename or path for the resulting ISO.
    /// When <c>null</c> the directory name is used.
    /// </summary>
    public string? Name;

    /// <summary>Next entry in a linked list of creation tasks, or <c>null</c>.</summary>
    public CreateList? Next;
}

/// <summary>
/// Represents a Windows FILETIME value as two 32-bit unsigned integers.
/// </summary>
public struct FileTime
{
    /// <summary>Low 32 bits of the FILETIME value.</summary>
    public uint Low;

    /// <summary>High 32 bits of the FILETIME value.</summary>
    public uint High;
}

/// <summary>
/// Context used during directory offset calculation for storing the
/// current sector position and directory start offset.
/// </summary>
public class WdsafpContext
{
    /// <summary>Directory start offset in bytes (sector * 2048).</summary>
    public long DirStart;

    /// <summary>Current sector counter being assigned.</summary>
    public uint CurrentSector;
}

/// <summary>
/// Context passed through the write-tree traversal, bundling the output stream,
/// optional source stream (for rewrite mode), progress callback, and path.
/// </summary>
public class WriteTreeContext
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
}
