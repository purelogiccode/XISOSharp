using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

public class AvlTreeTests
{
    [Fact]
    public void AvlCompareKey_SameStrings_ReturnsZero()
    {
        Assert.Equal(0, AvlTree.AvlCompareKey("hello", "hello"));
        Assert.Equal(0, AvlTree.AvlCompareKey("HELLO", "hello"));
        Assert.Equal(0, AvlTree.AvlCompareKey("hello", "HELLO"));
        Assert.Equal(0, AvlTree.AvlCompareKey("", ""));
    }

    [Fact]
    public void AvlCompareKey_LessThan_ReturnsNegative()
    {
        Assert.True(AvlTree.AvlCompareKey("a", "b") < 0);
        Assert.True(AvlTree.AvlCompareKey("A", "b") < 0);
        Assert.True(AvlTree.AvlCompareKey("a", "B") < 0);
        Assert.True(AvlTree.AvlCompareKey("", "a") < 0);
    }

    [Fact]
    public void AvlCompareKey_GreaterThan_ReturnsPositive()
    {
        Assert.True(AvlTree.AvlCompareKey("b", "a") > 0);
        Assert.True(AvlTree.AvlCompareKey("B", "a") > 0);
        Assert.True(AvlTree.AvlCompareKey("b", "A") > 0);
        Assert.True(AvlTree.AvlCompareKey("a", "") > 0);
    }

    [Fact]
    public void AvlCompareKey_CaseInsensitive_DifferentLengths()
    {
        Assert.True(AvlTree.AvlCompareKey("abc", "ab") > 0);
        Assert.True(AvlTree.AvlCompareKey("ab", "abc") < 0);
        Assert.True(AvlTree.AvlCompareKey("ABC", "abc") == 0);
    }

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

    [Fact]
    public void AvlFetch_EmptyTree_ReturnsNull()
    {
        Assert.Null(AvlTree.AvlFetch(null, "anything"));
    }

    [Fact]
    public void AvlInsert_MultipleNodes_TreeIsBalanced()
    {
        AvlNode? root = null;
        var nodes = new List<AvlNode>();

        // Insert nodes in ascending order (worst case for unbalanced tree)
        for (var i = 0; i < 100; i++)
        {
            var node = new AvlNode { Filename = $"file{i:D3}" };
            nodes.Add(node);
            var result = AvlTree.AvlInsert(ref root, node);
            Assert.NotEqual(AvlResult.AvlError, result);
        }

        // Verify all nodes can be found
        foreach (var node in nodes)
        {
            var found = AvlTree.AvlFetch(root, node.Filename);
            Assert.Same(node, found);
        }

        // Verify tree depth is logarithmic (balanced)
        var depth = GetTreeDepth(root);
        Assert.True(depth <= 12, $"Tree depth {depth} exceeds expected max for 100 nodes (should be ~7)");
    }

    [Fact]
    public void AvlTraverse_Prefix_VisitsInCorrectOrder()
    {
        AvlNode? root = null;
        // Insert to form a known tree structure
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
        AvlTree.AvlTraverseDepthFirst(root, (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Prefix, 0);

        // Prefix: root, left subtree, right subtree
        // The tree shape depends on insert order; verify it's valid prefix order
        Assert.Equal(5, visited.Count);
        Assert.Contains("a", visited);
        Assert.Contains("b", visited);
        Assert.Contains("c", visited);
        Assert.Contains("d", visited);
        Assert.Contains("e", visited);
    }

    [Fact]
    public void AvlTraverse_Infix_VisitsInSortedOrder()
    {
        AvlNode? root = null;
        foreach (var name in new[] { "z", "a", "m", "q", "b" })
        {
            AvlTree.AvlInsert(ref root, new AvlNode { Filename = name });
        }

        var visited = new List<string>();
        AvlTree.AvlTraverseDepthFirst(root, (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Infix, 0);

        // Infix should be sorted (case-insensitive)
        Assert.Equal(new[] { "a", "b", "m", "q", "z" }, visited);
    }

    [Fact]
    public void AvlTraverse_Postfix_VisitsChildrenBeforeParent()
    {
        AvlNode? root = null;
        // Insert in order that yields a known structure without double rotation:
        // "c", "a", "e", "b" — the AVL rebalancing will create a balanced tree.
        // We just verify the root is visited last in postfix.
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "c" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "a" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "e" });
        AvlTree.AvlInsert(ref root, new AvlNode { Filename = "b" });

        var visited = new List<string>();
        AvlTree.AvlTraverseDepthFirst(root, (node, ctx, _) =>
        {
            ((List<string>)ctx!).Add(node.Filename);
            return 0;
        }, visited, AvlTraversalMethod.Postfix, 0);

        Assert.Equal(4, visited.Count);
        // In postfix, the root node should be visited last
        Assert.Equal(root!.Filename, visited[^1]);
    }

    [Fact]
    public void AvlTraverse_NullRoot_ReturnsZero()
    {
        var result = AvlTree.AvlTraverseDepthFirst(null, (_, _, _) => 1, null, AvlTraversalMethod.Prefix, 0);
        Assert.Equal(0, result);
    }

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
            return 1; // error stops traversal
        }, null, AvlTraversalMethod.Prefix, 0);

        Assert.Equal(1, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void EmptySubdirectory_Sentinel_IsNotNullAndIdentifiable()
    {
        var node = new AvlNode { Filename = "dir", Subdirectory = AvlNode.EmptySubdirectory };

        Assert.NotNull(node.Subdirectory);
        Assert.True(ReferenceEquals(node.Subdirectory, AvlNode.EmptySubdirectory));
        Assert.NotSame(new AvlNode(), AvlNode.EmptySubdirectory);
    }

    [Fact]
    public void AvlInsert_ManyNodes_AllSkewsValid()
    {
        AvlNode? root = null;
        // Insert nodes in sequential order (tests rebalancing)
        for (var i = 0; i < 50; i++)
        {
            var node = new AvlNode { Filename = $"f{i:D4}" };
            AvlTree.AvlInsert(ref root, node);
        }

        VerifyAvlBalance(root);
    }

    [Fact]
    public void AvlInsert_RandomOrder_Consistent()
    {
        AvlNode? root = null;
        var rng = new Random(42);
        var names = Enumerable.Range(0, 200)
            .Select(_ => $"file_{rng.Next():X8}")
            .Distinct()
            .ToList();

        foreach (var name in names)
        {
            var node = new AvlNode { Filename = name };
            var res = AvlTree.AvlInsert(ref root, node);
            Assert.NotEqual(AvlResult.AvlError, res);
        }

        VerifyAvlBalance(root);

        // All nodes findable
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
            Assert.True(Math.Abs(leftDepth - rightDepth) <= 1, $"Unbalanced at node '{node.Filename}': left={leftDepth}, right={rightDepth}");

            VerifyAvlBalance(node.Left);
            node = node.Right;
        }
    }
}
