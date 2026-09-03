using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for the <see cref="AvlNode"/> data structure
/// and the <see cref="AvlSkew"/> enumeration.
/// </summary>
public class AvlNodeTests
{
    /// <summary>
    /// Verifies that <see cref="AvlSkew"/> enumeration values
    /// are 0 (NoSkew), 1 (LeftSkew), and 2 (RightSkew).
    /// </summary>
    [Fact]
    public void AvlSkew_Values()
    {
        Assert.Equal(0, (int)AvlSkew.NoSkew);
        Assert.Equal(1, (int)AvlSkew.LeftSkew);
        Assert.Equal(2, (int)AvlSkew.RightSkew);
    }

    /// <summary>
    /// Verifies that a newly constructed <see cref="AvlNode"/>
    /// has expected default values for all fields.
    /// </summary>
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

    /// <summary>
    /// Verifies that all properties of an <see cref="AvlNode"/>
    /// can be set via object initializer and read back correctly.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="AvlNode.EmptySubdirectory"/>
    /// is not null.
    /// </summary>
    [Fact]
    public void EmptySubdirectory_IsNotNull()
    {
        Assert.NotNull(AvlNode.EmptySubdirectory);
    }

    /// <summary>
    /// Verifies that <see cref="AvlNode.EmptySubdirectory"/>
    /// always returns the same singleton instance.
    /// </summary>
    [Fact]
    public void EmptySubdirectory_IsSingleton()
    {
        Assert.Same(AvlNode.EmptySubdirectory, AvlNode.EmptySubdirectory);
    }

    /// <summary>
    /// Verifies that <see cref="AvlNode.EmptySubdirectory"/>
    /// has default field values (empty filename, zero offset,
    /// null left/right children).
    /// </summary>
    [Fact]
    public void EmptySubdirectory_HasDefaults()
    {
        Assert.Equal("", AvlNode.EmptySubdirectory.Filename);
        Assert.Equal(0u, AvlNode.EmptySubdirectory.Offset);
        Assert.Null(AvlNode.EmptySubdirectory.Left);
        Assert.Null(AvlNode.EmptySubdirectory.Right);
    }

    /// <summary>
    /// Verifies that <see cref="AvlNode.EmptySubdirectory"/>
    /// is a distinct instance, not the same as a newly
    /// constructed <see cref="AvlNode"/>.
    /// </summary>
    [Fact]
    public void EmptySubdirectory_IsDistinctFromNewNode()
    {
        Assert.NotSame(new AvlNode(), AvlNode.EmptySubdirectory);
    }

    /// <summary>
    /// Verifies that the <see cref="AvlNode.Skew"/> property
    /// can be set to all three <see cref="AvlSkew"/> values
    /// (NoSkew, LeftSkew, RightSkew) and read back correctly.
    /// </summary>
    [Fact]
    public void AvlNode_AllSkewValues_CanBeSet()
    {
        var node = new AvlNode { Skew = AvlSkew.NoSkew };

        Assert.Equal(AvlSkew.NoSkew, node.Skew);

        node.Skew = AvlSkew.LeftSkew;
        Assert.Equal(AvlSkew.LeftSkew, node.Skew);

        node.Skew = AvlSkew.RightSkew;
        Assert.Equal(AvlSkew.RightSkew, node.Skew);
    }
}