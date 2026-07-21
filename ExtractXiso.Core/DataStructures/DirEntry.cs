namespace ExtractXiso.DataStructures;

/// <summary>
/// Represents an on-disk directory entry in an XISO image.
/// Directory entries are linked as a binary tree structure via the
/// <see cref="Left"/> and <see cref="Right"/> pointers, mirroring the
/// on-disk format of the original Xbox filesystem.
/// </summary>
public class DirEntry
{
    /// <summary>Pointer to the left child directory entry in the on-disk tree.</summary>
    public DirEntry? Left;

    /// <summary>Pointer to the parent directory entry.</summary>
    public DirEntry? Parent;

    /// <summary>Associated AVL node that indexes this entry by filename.</summary>
    public AvlNode? AvlNode;

    /// <summary>Filename of the file or directory.</summary>
    public string Filename = "";

    /// <summary>Right-child offset (in DWORDs) within the directory sector.</summary>
    public ushort ROffset;

    /// <summary>File attribute flags (e.g. <c>AttributeDIR</c>, <c>AttributeARC</c>).</summary>
    public byte Attributes;

    /// <summary>Length of the filename in bytes (ASCII).</summary>
    public byte FilenameLength;

    /// <summary>Size of the file in bytes, or size of the directory entry table for directories.</summary>
    public uint FileSize;

    /// <summary>Sector index where the file data or subdirectory begins.</summary>
    public uint StartSector;
}
