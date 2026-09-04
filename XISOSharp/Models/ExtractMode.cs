namespace XISOSharp.Models;

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