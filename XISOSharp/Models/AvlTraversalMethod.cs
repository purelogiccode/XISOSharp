namespace XISOSharp.Models;

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
