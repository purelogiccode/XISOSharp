using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

public class AvlTreeEdgeCasesTests
{
    [Fact]
    public void FreeTree_NullRoot_DoesNotThrow()
    {
        AvlTree.FreeTree(null);
    }

    [Fact]
    public void FreeTree_EmptySubdirectory_DoesNotThrow()
    {
        AvlTree.FreeTree(AvlNode.EmptySubdirectory);
    }

    [Fact]
    public void FreeTree_SimpleTree_CleansUpReferences()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "file1.txt", FileSize = 100 };
        var node2 = new AvlNode { Filename = "file2.txt", FileSize = 200 };

        AvlTree.AvlInsert(ref root, node1);
        AvlTree.AvlInsert(ref root, node2);

        Assert.NotNull(root);

        AvlTree.FreeTree(root);

        Assert.Null(node1.Left);
        Assert.Null(node1.Right);
        Assert.Null(node1.Subdirectory);
        Assert.Null(node2.Left);
        Assert.Null(node2.Right);
        Assert.Null(node2.Subdirectory);
    }

    [Fact]
    public void FreeTree_WithSubdirectory_CleansUpRecursively()
    {
        var subNode = new AvlNode { Filename = "subfile.txt", FileSize = 50 };
        AvlNode? subRoot = null;
        AvlTree.AvlInsert(ref subRoot, subNode);

        AvlNode? root = null;
        var dirNode = new AvlNode { Filename = "dir", Subdirectory = subRoot };
        AvlTree.AvlInsert(ref root, dirNode);

        AvlTree.FreeTree(root);

        Assert.Null(subNode.Left);
        Assert.Null(subNode.Right);
        Assert.Null(subNode.Subdirectory);
        Assert.Null(dirNode.Left);
        Assert.Null(dirNode.Right);
        Assert.Null(dirNode.Subdirectory);
    }

    [Fact]
    public void AvlFetch_NullFilename_ThrowsNullReferenceException()
    {
        AvlNode? root = null;
        var node = new AvlNode { Filename = "file.txt" };
        AvlTree.AvlInsert(ref root, node);

        Assert.Throws<NullReferenceException>(() => AvlTree.AvlFetch(root, null!));
    }

    [Fact]
    public void AvlFetch_EmptyKey_ReturnsNull_UnlessInsertedEmpty()
    {
        AvlNode? root = null;
        var result = AvlTree.AvlFetch(root, "");
        Assert.Null(result);
    }

    [Fact]
    public void AvlTraverseDepthFirst_InvalidMethod_ReturnsZero()
    {
        AvlNode? root = null;
        var node = new AvlNode { Filename = "test" };
        AvlTree.AvlInsert(ref root, node);

        var callCount = 0;

        var result = AvlTree.AvlTraverseDepthFirst(root, Cb, null, (AvlTraversalMethod)99, 0);
        Assert.Equal(0, result);
        Assert.Equal(0, callCount);
        return;

        int Cb(AvlNode n, object? ctx, int depth)
        {
            callCount++;
            return 0;
        }
    }

    [Fact]
    public void AvlCompareKey_SameString_ReturnsZero()
    {
        const string same = "test";

        Assert.Equal(0, AvlTree.AvlCompareKey(same, same));
    }

    [Fact]
    public void AvlCompareKey_DifferentLengths()
    {
        const string shorter = "abc";
        const string longer = "abc123";

        Assert.True(AvlTree.AvlCompareKey(shorter, longer) < 0);
        Assert.True(AvlTree.AvlCompareKey(longer, shorter) > 0);
    }

    [Fact]
    public void AvlCompareKey_CaseInsensitiveComparison()
    {
        const string a = "TEST";
        const string b = "Test";
        const string c = "test";
        const string d = "TESU";

        Assert.Equal(0, AvlTree.AvlCompareKey(a, b));
        Assert.Equal(0, AvlTree.AvlCompareKey(b, c));
        Assert.True(AvlTree.AvlCompareKey(a, d) < 0);
        Assert.True(AvlTree.AvlCompareKey(d, a) > 0);
    }

    [Fact]
    public void AvlTraverseDepthFirst_NullRoot_AllMethods_ReturnZero()
    {
        var callCount = 0;
        TraversalCallback cb = (n, ctx, depth) =>
        {
            callCount++;
            return 0;
        };

        Assert.Equal(0, AvlTree.AvlTraverseDepthFirst(null, cb, null, AvlTraversalMethod.Prefix, 0));
        Assert.Equal(0, AvlTree.AvlTraverseDepthFirst(null, cb, null, AvlTraversalMethod.Infix, 0));
        Assert.Equal(0, AvlTree.AvlTraverseDepthFirst(null, cb, null, AvlTraversalMethod.Postfix, 0));
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void AvlInsert_Duplicate_ReturnsAvlError()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "file.txt" };
        var node2 = new AvlNode { Filename = "file.txt" };

        Assert.Equal(AvlResult.AvlBalanced, AvlTree.AvlInsert(ref root, node1));
        Assert.Equal(AvlResult.AvlError, AvlTree.AvlInsert(ref root, node2));
    }

    [Fact]
    public void AvlInsert_CaseInsensitiveDuplicate_ReturnsAvlError()
    {
        AvlNode? root = null;
        var node1 = new AvlNode { Filename = "File.TXT" };
        var node2 = new AvlNode { Filename = "file.txt" };

        Assert.Equal(AvlResult.AvlBalanced, AvlTree.AvlInsert(ref root, node1));
        Assert.Equal(AvlResult.AvlError, AvlTree.AvlInsert(ref root, node2));
    }
}
