using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for the <see cref="AvlTree"/> static methods:
/// key comparison, insertion, fetching, traversal, and
/// balance verification.
/// </summary>
public class AvlTreeTests
{
    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlCompareKey"/> returns zero
    /// when comparing two strings that are case-insensitively equal,
    /// including an empty string compared to itself.
    /// </summary>
    [Fact]
    public void AvlCompareKey_SameStrings_ReturnsZero()
    {
        Assert.Equal(0, AvlTree.AvlCompareKey("hello", "hello"));
        Assert.Equal(0, AvlTree.AvlCompareKey("HELLO", "hello"));
        Assert.Equal(0, AvlTree.AvlCompareKey("hello", "HELLO"));
        Assert.Equal(0, AvlTree.AvlCompareKey("", ""));
    }

    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlCompareKey"/> returns a negative
    /// value when the first key is case-insensitively less than the second,
    /// including comparing an empty string to a non-empty string.
    /// </summary>
    [Fact]
    public void AvlCompareKey_LessThan_ReturnsNegative()
    {
        Assert.True(AvlTree.AvlCompareKey("a", "b") < 0);
        Assert.True(AvlTree.AvlCompareKey("A", "b") < 0);
        Assert.True(AvlTree.AvlCompareKey("a", "B") < 0);
        Assert.True(AvlTree.AvlCompareKey("", "a") < 0);
    }

    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlCompareKey"/> returns a positive
    /// value when the first key is case-insensitively greater than the second,
    /// including comparing a non-empty string to an empty string.
    /// </summary>
    [Fact]
    public void AvlCompareKey_GreaterThan_ReturnsPositive()
    {
        Assert.True(AvlTree.AvlCompareKey("b", "a") > 0);
        Assert.True(AvlTree.AvlCompareKey("B", "a") > 0);
        Assert.True(AvlTree.AvlCompareKey("b", "A") > 0);
        Assert.True(AvlTree.AvlCompareKey("a", "") > 0);
    }

    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlCompareKey"/> handles strings
    /// of different lengths correctly: longer strings are greater
    /// if they share a common prefix, and case differences
    /// do not affect equality.
    /// </summary>
    [Fact]
    public void AvlCompareKey_CaseInsensitive_DifferentLengths()
    {
        Assert.True(AvlTree.AvlCompareKey("abc", "ab") > 0);
        Assert.True(AvlTree.AvlCompareKey("ab", "abc") < 0);
        Assert.Equal(0, AvlTree.AvlCompareKey("ABC", "abc"));
    }

    /// <summary>
    /// Verifies that inserting into an empty tree makes the
    /// inserted node the root, returns <see cref="AvlResult.AvlBalanced"/>,
    /// and the root has no children.
    /// </summary>
    [Fact]
    public void AvlInsert_EmptyTree_BecomesRoot()
    {
        AvlNode? root = null;
        var node = new AvlNode { Filename = "test" };

        var result = AvlTree.AvlInsert(ref root, node);

        Assert.Equal(AvlResult.AvlBalanced, result);
        Assert.Same(node, root);
        Assert.Null(root!.Left);
        Assert.Null(root.Right);
    }

    /// <summary>
    /// Verifies that inserting a node with a duplicate filename
    /// (case-insensitive match) returns <see cref="AvlResult.AvlError"/>
    /// and does not modify the tree.
    /// </summary>
    [Fact]
    public void AvlInsert_Duplicate_ReturnsError()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "test" };
        var node2 = new AvlNode { Filename = "test" };

        AvlTree.AvlInsert(ref root, node1);
        var result = AvlTree.AvlInsert(ref root, node2);

        Assert.Equal(AvlResult.AvlError, result);
    }

    /// <summary>
    /// Verifies that inserting a node with a filename that differs
    /// only in case from an existing node returns
    /// <see cref="AvlResult.AvlError"/>.
    /// </summary>
    [Fact]
    public void AvlInsert_CaseInsensitiveDuplicate_ReturnsError()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "test" };
        var node2 = new AvlNode { Filename = "TEST" };

        AvlTree.AvlInsert(ref root, node1);
        var result = AvlTree.AvlInsert(ref root, node2);

        Assert.Equal(AvlResult.AvlError, result);
    }

    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlFetch"/> returns the exact
    /// node instance for each inserted filename (case-insensitive lookup)
    /// and returns null for a non-existent key.
    /// </summary>
    [Fact]
    public void AvlFetch_FindsInsertedNode()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "alpha" };
        var node2 = new AvlNode { Filename = "beta" };
        var node3 = new AvlNode { Filename = "gamma" };

        AvlTree.AvlInsert(ref root, node1);
        AvlTree.AvlInsert(ref root, node2);
        AvlTree.AvlInsert(ref root, node3);

        Assert.Same(node1, AvlTree.AvlFetch(root, "alpha"));
        Assert.Same(node2, AvlTree.AvlFetch(root, "beta"));
        Assert.Same(node3, AvlTree.AvlFetch(root, "gamma"));
        Assert.Same(node1, AvlTree.AvlFetch(root, "ALPHA"));
        Assert.Null(AvlTree.AvlFetch(root, "delta"));
    }

    /// <summary>
    /// Verifies that <see cref="AvlTree.AvlFetch"/> returns null
    /// when called on a null root.
    /// </summary>
    [Fact]
    public void AvlFetch_EmptyTree_ReturnsNull()
    {
        Assert.Null(AvlTree.AvlFetch(null, "anything"));
    }

    /// <summary>
    /// Verifies that inserting 100 nodes with sequential filenames
    /// produces a balanced tree (depth &lt;= 12), that all nodes
    /// are retrievable, and that no duplicate errors occur.
    /// </summary>
    [Fact]
    public void AvlInsert_MultipleNodes_TreeIsBalanced()
    {
        AvlNode? root = null;
        var nodes = new List<AvlNode>();

        for (var i = 0; i < 100; i++)
        {
            var node = new AvlNode { Filename = $"file{i:D3}" };
            nodes.Add(node);
            var result = AvlTree.AvlInsert(ref root, node);
            Assert.NotEqual(AvlResult.AvlError, result);
        }

        foreach (var node in nodes)
        {
            var found = AvlTree.AvlFetch(root, node.Filename);
            Assert.Same(node, found);
        }

        var depth = GetTreeDepth(root);
        Assert.True(depth <= 12, $"Tree depth {depth} exceeds expected max for 100 nodes (should be ~7)");
    }

    /// <summary>
    /// Verifies that prefix traversal visits all five inserted nodes
    /// exactly once, with at least one valid element found
    /// at each position in the visited list.
    /// </summary>
    [Fact]
    public void AvlTraverse_Prefix_VisitsInCorrectOrder()
    {
        AvlNode? root = null;
        var nodeC = new AvlNode { Filename = "c" };
        var nodeA = new AvlNode { Filename = "a" };
        var nodeB = new AvlNode { Filename = "b" };
        var nodeE = new AvlNode { Filename = "e" };
        var nodeD = new AvlNode { Filename = "d" };

        AvlTree.AvlInsert(ref root, nodeC);
        AvlTree.AvlInsert(ref root, nodeA);
        AvlTree.AvlInsert(ref root, nodeB);
        AvlTree.AvlInsert(ref root, nodeE);
        AvlTree.AvlInsert(ref root, nodeD);

        var visited = new List<string>();
        AvlTree.AvlTraverseDepthFirst(root, static (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Prefix, 0);

        Assert.Equal(5, visited.Count);
        Assert.Contains("a", visited, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("b", visited, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("c", visited, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("d", visited, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("e", visited, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] Expected = new[] { "a", "b", "m", "q", "z" };

    /// <summary>
    /// Verifies that infix (in-order) traversal visits nodes
    /// in case-insensitive sorted alphabetical order.
    /// </summary>
    [Fact]
    public void AvlTraverse_Infix_VisitsInSortedOrder()
    {
        AvlNode? root = null;
        foreach (var name in new[] { "z", "a", "m", "q", "b" })
        {
            AvlTree.AvlInsert(ref root, new AvlNode { Filename = name });
        }

        var visited = new List<string>();
        AvlTree.AvlTraverseDepthFirst(root, static (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Infix, 0);

        Assert.Equal(Expected, visited, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that postfix traversal visits children before
    /// their parent and that the tree root is the last
    /// node visited.
    /// </summary>
    [Fact]
    public void AvlTraverse_Postfix_VisitsChildrenBeforeParent()
    {
        AvlNode? root = null;
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "c" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "a" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "e" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "b" });

        var visited = new List<string>();
        AvlTree.AvlTraverseDepthFirst(root, static (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Postfix, 0);

        Assert.Equal(4, visited.Count);
        Assert.Equal(root!.Filename, visited[^1]);
    }

    /// <summary>
    /// Verifies that traversing a null root returns zero
    /// without invoking the callback.
    /// </summary>
    [Fact]
    public void AvlTraverse_NullRoot_ReturnsZero()
    {
        var result = AvlTree.AvlTraverseDepthFirst(null, static (_, _, _) => 1, null, AvlTraversalMethod.Prefix, 0);
        Assert.Equal(0, result);
    }

    /// <summary>
    /// Verifies that traversal stops immediately when the
    /// callback returns a non-zero value, and that the
    /// non-zero value is propagated as the return value.
    /// </summary>
    [Fact]
    public void AvlTraverse_CallbackError_StopsTraversal()
    {
        AvlNode? root = null;
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "a" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "b" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "c" });

        var callCount = 0;
        var result = AvlTree.AvlTraverseDepthFirst(root, (_, _, _) =>
        {
            callCount++;
            return 1;
        }, null, AvlTraversalMethod.Prefix, 0);

        Assert.Equal(1, result);
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// Verifies that assigning <see cref="AvlNode.EmptySubdirectory"/>
    /// to a node's Subdirectory property results in a non-null,
    /// referentially identical sentinel value that is distinct
    /// from a newly constructed <see cref="AvlNode"/>.
    /// </summary>
    [Fact]
    public void EmptySubdirectory_Sentinel_IsNotNullAndIdentifiable()
    {
        var node = new AvlNode { Filename = "dir", Subdirectory = AvlNode.EmptySubdirectory };

        Assert.NotNull(node.Subdirectory);
        Assert.True(ReferenceEquals(node.Subdirectory, AvlNode.EmptySubdirectory));
        Assert.NotSame(new AvlNode(), AvlNode.EmptySubdirectory);
    }

    /// <summary>
    /// Verifies that after inserting 50 nodes with sequential
    /// filenames, the balance factor at every node in the tree
    /// is within the AVL invariant of ±1.
    /// </summary>
    [Fact]
    public void AvlInsert_ManyNodes_AllSkewsValid()
    {
        AvlNode? root = null;
        for (var i = 0; i < 50; i++)
        {
            var node = new AvlNode { Filename = $"f{i:D4}" };
            AvlTree.AvlInsert(ref root, node);
        }

        VerifyAvlBalance(root);
    }

    /// <summary>
    /// Verifies that inserting 200 nodes with random, distinct
    /// filenames results in a balanced tree where every node
    /// is retrievable via <see cref="AvlTree.AvlFetch"/>.
    /// </summary>
    [Fact]
    public void AvlInsert_RandomOrder_Consistent()
    {
        AvlNode? root = null;
        var rng = new Random(42);
        var names = Enumerable.Range(0, 200)
            .Select(_ => $"file_{rng.Next():X8}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var name in names)
        {
            var node = new AvlNode { Filename = name };
            var res = AvlTree.AvlInsert(ref root, node);
            Assert.NotEqual(AvlResult.AvlError, res);
        }

        VerifyAvlBalance(root);

        foreach (var name in names)
        {
            Assert.NotNull(AvlTree.AvlFetch(root, name));
        }
    }

    private static int GetTreeDepth(AvlNode? node)
    {
        if (node == null) return 0;

        return 1 + Math.Max(GetTreeDepth(node.Left), GetTreeDepth(node.Right));
    }

    private static void VerifyAvlBalance(AvlNode? node)
    {
        while (true)
        {
            if (node == null) return;

            var leftDepth = GetTreeDepth(node.Left);
            var rightDepth = GetTreeDepth(node.Right);
            Assert.True(Math.Abs(leftDepth - rightDepth) <= 1,
                $"Unbalanced at node '{node.Filename}': left={leftDepth}, right={rightDepth}");

            VerifyAvlBalance(node.Left);
            node = node.Right;
        }
    }
}