using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

public class AvlNodeTests
{
    [Fact]
    public void AvlSkew_Values()
    {
        Assert.Equal(0, (int)AvlSkew.NoSkew);
        Assert.Equal(1, (int)AvlSkew.LeftSkew);
        Assert.Equal(2, (int)AvlSkew.RightSkew);
    }

    [Fact]
    public void New_AvlNode_HasDefaults()
    {
        var node = new AvlNode();

        Assert.Equal(0u, node.Offset);
        Assert.Equal(0, node.DirStart);
        Assert.Equal("", node.Filename);
        Assert.Equal(0u, node.FileSize);
        Assert.Equal(0u, node.StartSector);
        Assert.Null(node.Subdirectory);
        Assert.Equal(0u, node.OldStartSector);
        Assert.Equal(AvlSkew.NoSkew, node.Skew);
        Assert.Null(node.Left);
        Assert.Null(node.Right);
    }

    [Fact]
    public void AvlNode_FieldsCanBeSet()
    {
        var left = new AvlNode();
        var right = new AvlNode();
        var subdir = new AvlNode();

        var node = new AvlNode
        {
            Offset = 100,
            DirStart = 0x1000,
            Filename = "game.iso",
            FileSize = 2048,
            StartSector = 5,
            Subdirectory = subdir,
            OldStartSector = 3,
            Skew = AvlSkew.LeftSkew,
            Left = left,
            Right = right
        };

        Assert.Equal(100u, node.Offset);
        Assert.Equal(0x1000, node.DirStart);
        Assert.Equal("game.iso", node.Filename);
        Assert.Equal(2048u, node.FileSize);
        Assert.Equal(5u, node.StartSector);
        Assert.Same(subdir, node.Subdirectory);
        Assert.Equal(3u, node.OldStartSector);
        Assert.Equal(AvlSkew.LeftSkew, node.Skew);
        Assert.Same(left, node.Left);
        Assert.Same(right, node.Right);
    }

    [Fact]
    public void EmptySubdirectory_IsNotNull()
    {
        Assert.NotNull(AvlNode.EmptySubdirectory);
    }

    [Fact]
    public void EmptySubdirectory_IsSingleton()
    {
        Assert.Same(AvlNode.EmptySubdirectory, AvlNode.EmptySubdirectory);
    }

    [Fact]
    public void EmptySubdirectory_HasDefaults()
    {
        Assert.Equal("", AvlNode.EmptySubdirectory.Filename);
        Assert.Equal(0u, AvlNode.EmptySubdirectory.Offset);
        Assert.Null(AvlNode.EmptySubdirectory.Left);
        Assert.Null(AvlNode.EmptySubdirectory.Right);
    }

    [Fact]
    public void EmptySubdirectory_IsDistinctFromNewNode()
    {
        Assert.NotSame(new AvlNode(), AvlNode.EmptySubdirectory);
    }

    [Fact]
    public void AvlNode_AllSkewValues_CanBeSet()
    {
        var node = new AvlNode();

        node.Skew = AvlSkew.NoSkew;
        Assert.Equal(AvlSkew.NoSkew, node.Skew);

        node.Skew = AvlSkew.LeftSkew;
        Assert.Equal(AvlSkew.LeftSkew, node.Skew);

        node.Skew = AvlSkew.RightSkew;
        Assert.Equal(AvlSkew.RightSkew, node.Skew);
    }
}
