namespace XISOSharp.DataStructures;

/// <summary>Skew direction of an AVL tree node, used during balancing.</summary>
public enum AvlSkew
{
    /// <summary>Node is balanced (left and right subtrees have equal height).</summary>
    NoSkew,
    /// <summary>Left subtree is taller than the right subtree.</summary>
    LeftSkew,
    /// <summary>Right subtree is taller than the left subtree.</summary>
    RightSkew
}
