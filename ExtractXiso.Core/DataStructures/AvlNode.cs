namespace ExtractXiso.DataStructures;

/// <summary>Skew direction of an AVL tree node, used during balancing.</summary>
public enum AvlSkew { NoSkew, LeftSkew, RightSkew }

/// <summary>
/// Node in an AVL (Adelson-Velsky/Landis) balanced binary search tree.
/// Used to index XISO directory entries by filename for fast lookup.
/// </summary>
public class AvlNode
{
    /// <summary>
    /// Singleton sentinel node that represents an empty subdirectory.
    /// Never contains files or further children.
    /// </summary>
    public static readonly AvlNode EmptySubdirectory = new();

    /// <summary>Byte offset of this node's directory entry within its parent sector.</summary>
    public uint Offset;

    /// <summary>Start byte position of the directory table this node belongs to.</summary>
    public long DirStart;

    /// <summary>Filename (case-insensitive key for the AVL tree).</summary>
    public string Filename = "";

    /// <summary>Size of the file in bytes, or size of the directory entry table for directories.</summary>
    public uint FileSize;

    /// <summary>Sector index where the file data or subdirectory table begins in the XISO image.</summary>
    public uint StartSector;

    /// <summary>
    /// Root of an AVL tree containing the children of this directory node,
    /// or <see cref="EmptySubdirectory"/> if the directory is empty.
    /// </summary>
    public AvlNode? Subdirectory;

    /// <summary>Original sector position before rewrite; used when rebuilding from an existing ISO.</summary>
    public uint OldStartSector;

    /// <summary>Current balance state of this node.</summary>
    public AvlSkew Skew;

    /// <summary>Left child in the AVL tree.</summary>
    public AvlNode? Left;

    /// <summary>Right child in the AVL tree.</summary>
    public AvlNode? Right;
}
