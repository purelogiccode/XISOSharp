using XISOSharp.DataStructures;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <see cref="DirEntry"/> data structure, verifying default values,
/// field assignment, sibling linking, and parent chain relationships.
/// </summary>
public class DirEntryTests
{
    /// <summary>
    /// Verifies that a newly constructed <see cref="DirEntry"/> has expected default
    /// values for all fields.
    /// </summary>
    [Fact]
    public void New_DirEntry_HasDefaults()
    {
        var entry = new DirEntry();

        Assert.Null(entry.Left);
        Assert.Null(entry.Parent);
        Assert.Null(entry.AvlNode);
        Assert.Equal("", entry.Filename);
        Assert.Equal((ushort)0, entry.ROffset);
        Assert.Equal((byte)0, entry.Attributes);
        Assert.Equal((byte)0, entry.FilenameLength);
        Assert.Equal(0u, entry.FileSize);
        Assert.Equal(0u, entry.StartSector);
    }

    /// <summary>
    /// Verifies that all <see cref="DirEntry"/> fields can be individually set
    /// and retain their assigned values.
    /// </summary>
    [Fact]
    public void DirEntry_FieldsCanBeSet()
    {
        var left = new DirEntry();
        var parent = new DirEntry();
        var avlNode = new AvlNode();

        var entry = new DirEntry
        {
            Left = left,
            Parent = parent,
            AvlNode = avlNode,
            Filename = "test.xbe",
            ROffset = 0x800,
            Attributes = 0x01,
            FilenameLength = 8,
            FileSize = 1024,
            StartSector = 5
        };

        Assert.Same(left, entry.Left);
        Assert.Same(parent, entry.Parent);
        Assert.Same(avlNode, entry.AvlNode);
        Assert.Equal("test.xbe", entry.Filename);
        Assert.Equal((ushort)0x800, entry.ROffset);
        Assert.Equal((byte)0x01, entry.Attributes);
        Assert.Equal((byte)8, entry.FilenameLength);
        Assert.Equal(1024u, entry.FileSize);
        Assert.Equal(5u, entry.StartSector);
    }

    /// <summary>
    /// Verifies that <see cref="DirEntry"/> instances can be linked through the
    /// <see cref="DirEntry.Left"/> property to form a sibling chain.
    /// </summary>
    [Fact]
    public void DirEntry_LinkedLeftSiblings()
    {
        var a = new DirEntry { Filename = "a" };
        var b = new DirEntry { Filename = "b", Left = a };
        var c = new DirEntry { Filename = "c", Left = b };

        Assert.Same(b, c.Left);
        Assert.Same(a, c.Left.Left);
    }

    /// <summary>
    /// Verifies that <see cref="DirEntry"/> instances can be linked through the
    /// <see cref="DirEntry.Parent"/> property to form a parent-child hierarchy.
    /// </summary>
    [Fact]
    public void DirEntry_ParentChain()
    {
        var leaf = new DirEntry { Filename = "leaf" };
        var dir = new DirEntry { Filename = "dir" };
        leaf.Parent = dir;

        Assert.Same(dir, leaf.Parent);
        Assert.Null(dir.Parent);
    }
}
