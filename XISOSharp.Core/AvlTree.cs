using XISOSharp.DataStructures;

namespace XISOSharp;

/// <summary>
/// AVL balanced binary search tree implementation for key-based insertion,
/// lookup, and traversal of XISO directory entries.
/// Keys are case-insensitive ASCII strings.
/// </summary>
public static class AvlTree
{
    /// <summary>
    /// Compares two strings case-insensitively using ASCII rules.
    /// Shorter strings are considered "less than" longer ones when the
    /// common prefix matches.
    /// </summary>
    /// <param name="lhs">Left-hand side string.</param>
    /// <param name="rhs">Right-hand side string.</param>
    /// <returns>
    /// Negative if <paramref name="lhs"/> &lt; <paramref name="rhs"/>,
    /// zero if equal, positive if <paramref name="lhs"/> &gt; <paramref name="rhs"/>.
    /// </returns>
    public static int AvlCompareKey(string lhs, string rhs)
    {
        var i = 0;
        while (true)
        {
            var a = i < lhs.Length ? lhs[i] : '\0';
            var b = i < rhs.Length ? rhs[i] : '\0';
            i++;

            if (a is >= 'a' and <= 'z')
            {
                a = (char)(a - 32);
            }

            if (b is >= 'a' and <= 'z')
            {
                b = (char)(b - 32);
            }

            if (a != 0)
            {
                if (b != 0)
                {
                    if (a < b) return -1;
                    if (a > b) return 1;
                }
                else
                {
                    return 1;
                }
            }
            else
            {
                return b != 0 ? -1 : 0;
            }
        }
    }

    /// <summary>
    /// Looks up a node in the AVL tree by filename.
    /// </summary>
    /// <param name="root">Root of the tree (may be <c>null</c>).</param>
    /// <param name="filename">Filename to search for (case-insensitive).</param>
    /// <returns>The matching <see cref="AvlNode"/> or <c>null</c> if not found.</returns>
    public static AvlNode? AvlFetch(AvlNode? root, string filename)
    {
        while (true)
        {
            if (root == null) return null;

            var result = AvlCompareKey(filename, root.Filename);

            switch (result)
            {
                case < 0:
                    root = root.Left;
                    break;
                case > 0:
                    root = root.Right;
                    break;
                default:
                    return root;
            }
        }
    }

    /// <summary>
    /// Inserts an <see cref="AvlNode"/> into the AVL tree, rebalancing as needed.
    /// Duplicate filenames (case-insensitive) are rejected.
    /// </summary>
    /// <param name="root">Reference to the tree root.</param>
    /// <param name="node">Node to insert.</param>
    /// <returns>
    /// <see cref="AvlResult.AvlBalanced"/> if the tree grew taller,
    /// <see cref="AvlResult.NoErr"/> if insertion completed without height change,
    /// <see cref="AvlResult.AvlError"/> on duplicate key.
    /// </returns>
    public static AvlResult AvlInsert(ref AvlNode? root, AvlNode node)
    {
        if (root == null)
        {
            root = node;
            return AvlResult.AvlBalanced;
        }

        var result = AvlCompareKey(node.Filename, root.Filename);

        switch (result)
        {
            case < 0:
            {
                var tmp = AvlInsert(ref root.Left, node);
                return tmp == AvlResult.AvlBalanced ? AvlLeftGrown(ref root) : tmp;
            }
            case > 0:
            {
                var tmp = AvlInsert(ref root.Right, node);
                return tmp == AvlResult.AvlBalanced ? AvlRightGrown(ref root) : tmp;
            }
            default:
                return AvlResult.AvlError;
        }
    }

    /// <summary>
    /// Handles rebalancing after the left subtree grew taller.
    /// Performs single right rotation (LL case) or double rotation (LR case).
    /// </summary>
    private static AvlResult AvlLeftGrown(ref AvlNode root)
    {
        switch (root.Skew)
        {
            case AvlSkew.LeftSkew:
            {
                if (root.Left!.Skew == AvlSkew.LeftSkew)
                {
                    root.Skew = root.Left.Skew = AvlSkew.NoSkew;
                    AvlRotateRight(ref root);
                }
                else
                {
                    switch (root.Left!.Right!.Skew)
                    {
                        case AvlSkew.LeftSkew:
                            root.Skew = AvlSkew.RightSkew;
                            root.Left.Skew = AvlSkew.NoSkew;
                            break;

                        case AvlSkew.RightSkew:
                            root.Skew = AvlSkew.NoSkew;
                            root.Left.Skew = AvlSkew.LeftSkew;
                            break;

                        default:
                            root.Skew = AvlSkew.NoSkew;
                            root.Left.Skew = AvlSkew.NoSkew;
                            break;
                    }

                    root.Left.Right.Skew = AvlSkew.NoSkew;
                    var left = root.Left;
                    AvlRotateLeft(ref left);
                    root.Left = left;
                    AvlRotateRight(ref root);
                }

                return AvlResult.NoErr;
            }

            case AvlSkew.RightSkew:
                root.Skew = AvlSkew.NoSkew;
                return AvlResult.NoErr;

            default:
                root.Skew = AvlSkew.LeftSkew;
                return AvlResult.AvlBalanced;
        }
    }

    /// <summary>
    /// Handles rebalancing after the right subtree grew taller.
    /// Performs single left rotation (RR case) or double rotation (RL case).
    /// </summary>
    private static AvlResult AvlRightGrown(ref AvlNode root)
    {
        switch (root.Skew)
        {
            case AvlSkew.LeftSkew:
                root.Skew = AvlSkew.NoSkew;
                return AvlResult.NoErr;

            case AvlSkew.RightSkew:
            {
                if (root.Right!.Skew == AvlSkew.RightSkew)
                {
                    root.Skew = root.Right.Skew = AvlSkew.NoSkew;
                    AvlRotateLeft(ref root);
                }
                else
                {
                    switch (root.Right!.Left!.Skew)
                    {
                        case AvlSkew.LeftSkew:
                            root.Skew = AvlSkew.NoSkew;
                            root.Right.Skew = AvlSkew.RightSkew;
                            break;

                        case AvlSkew.RightSkew:
                            root.Skew = AvlSkew.LeftSkew;
                            root.Right.Skew = AvlSkew.NoSkew;
                            break;

                        default:
                            root.Skew = AvlSkew.NoSkew;
                            root.Right.Skew = AvlSkew.NoSkew;
                            break;
                    }

                    root.Right.Left.Skew = AvlSkew.NoSkew;
                    var right = root.Right;
                    AvlRotateRight(ref right);
                    root.Right = right;
                    AvlRotateLeft(ref root);
                }

                return AvlResult.NoErr;
            }

            default:
                root.Skew = AvlSkew.RightSkew;
                return AvlResult.AvlBalanced;
        }
    }

    /// <summary>
    /// Performs a left rotation around the given node.
    /// </summary>
    private static void AvlRotateLeft(ref AvlNode root)
    {
        var tmp = root;
        root = root.Right!;
        tmp.Right = root.Left;
        root.Left = tmp;
    }

    /// <summary>
    /// Performs a right rotation around the given node.
    /// </summary>
    private static void AvlRotateRight(ref AvlNode root)
    {
        var tmp = root;
        root = root.Left!;
        tmp.Left = root.Right;
        root.Right = tmp;
    }

    /// <summary>
    /// Traverses the AVL tree depth-first in the specified order, invoking a
    /// callback for each node visited.
    /// </summary>
    /// <param name="root">Root of the tree (may be <c>null</c>).</param>
    /// <param name="callback">
    /// Callback invoked per node. Return non-zero to stop traversal.
    /// </param>
    /// <param name="context">Arbitrary context passed to the callback.</param>
    /// <param name="method">Traversal order: prefix, infix, or postfix.</param>
    /// <param name="depth">Starting depth (typically 0).</param>
    /// <returns>
    /// 0 if the full traversal completed, or the non-zero value returned by the callback.
    /// An unknown traversal method returns 0 without visiting any nodes.
    /// </returns>
    public static int AvlTraverseDepthFirst(
        AvlNode? root,
        TraversalCallback callback,
        object? context,
        AvlTraversalMethod method,
        int depth)
    {
        if (root == null) return 0;

        int err;

        switch (method)
        {
            case AvlTraversalMethod.Prefix:
                err = callback(root, context, depth);
                if (err == 0)
                {
                    err = AvlTraverseDepthFirst(root.Left, callback, context, method, depth + 1);
                }

                if (err == 0)
                {
                    err = AvlTraverseDepthFirst(root.Right, callback, context, method, depth + 1);
                }

                break;

            case AvlTraversalMethod.Infix:
                err = AvlTraverseDepthFirst(root.Left, callback, context, method, depth + 1);
                if (err == 0)
                {
                    err = callback(root, context, depth);
                }

                if (err == 0)
                {
                    err = AvlTraverseDepthFirst(root.Right, callback, context, method, depth + 1);
                }

                break;

            case AvlTraversalMethod.Postfix:
                err = AvlTraverseDepthFirst(root.Left, callback, context, method, depth + 1);
                if (err == 0)
                {
                    err = AvlTraverseDepthFirst(root.Right, callback, context, method, depth + 1);
                }

                if (err == 0)
                {
                    err = callback(root, context, depth);
                }

                break;

            default:
                err = 0;
                break;
        }

        return err;
    }

    /// <summary>
    /// Traversal callback that recursively frees all nodes in a subdirectory tree.
    /// Clears child references so the GC can collect them.
    /// Intended to be used as the callback for <see cref="AvlTraverseDepthFirst"/>
    /// in postfix order.
    /// </summary>
    /// <param name="node">Current node being freed.</param>
    /// <param name="context">Not used.</param>
    /// <param name="depth">Not used.</param>
    /// <returns>Always 0.</returns>
    internal static int FreeDirNodeAvl(AvlNode node, object? context, int depth)
    {
        if (node.Subdirectory != null && !ReferenceEquals(node.Subdirectory, AvlNode.EmptySubdirectory))
        {
            AvlTraverseDepthFirst(node.Subdirectory, FreeDirNodeAvl, null, AvlTraversalMethod.Postfix, 0);
        }

        node.Left = null;
        node.Right = null;
        node.Subdirectory = null;

        return 0;
    }

    /// <summary>
    /// Frees an entire AVL tree by traversing in postfix order and clearing all
    /// node references so the garbage collector can reclaim memory.
    /// </summary>
    /// <param name="root">Root of the tree to free. Passed through safely if <c>null</c> or <see cref="AvlNode.EmptySubdirectory"/>.</param>
    public static void FreeTree(AvlNode? root)
    {
        if (root != null && !ReferenceEquals(root, AvlNode.EmptySubdirectory))
        {
            AvlTraverseDepthFirst(root, FreeDirNodeAvl, null, AvlTraversalMethod.Postfix, 0);
        }
    }
}
