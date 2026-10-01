using System.Buffers.Binary;
using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="DirectoryEntryTableWriter"/>: entry validation,
/// offset placement, encoding layout, and table serialization edge cases.
/// </summary>
public class DirectoryEntryTableWriterExtraTests
{
    private static AvlNode Node(string name, bool isDirectory = false, uint startSector = 0,
        uint fileSize = 0, byte attributes = 0) => new()
    {
        Filename = name,
        Subdirectory = isDirectory ? new AvlNode() : null,
        StartSector = startSector,
        FileSize = fileSize,
        Attributes = attributes,
    };

    /// <summary>Verifies an empty entry set produces a null root.</summary>
    [Fact]
    public void BuildTable_Empty_ReturnsNull()
    {
        Assert.Null(DirectoryEntryTableWriter.BuildTable([]));
    }

    /// <summary>Verifies invalid names are rejected during table construction.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void BuildTable_InvalidName_Throws(string name)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DirectoryEntryTableWriter.BuildTable([
                new DirectoryEntryTableWriter.DirectoryTableEntry(name, false, 0, 0)
            ]));
    }

    /// <summary>Verifies a name longer than the maximum is rejected.</summary>
    [Fact]
    public void BuildTable_NameTooLong_Throws()
    {
        string name = new('a', Constants.FilenameMaxChars + 1);
        Assert.Throws<InvalidOperationException>(() =>
            DirectoryEntryTableWriter.BuildTable([
                new DirectoryEntryTableWriter.DirectoryTableEntry(name, false, 0, 0)
            ]));
    }

    /// <summary>Verifies duplicate names differing only by case are rejected.</summary>
    [Fact]
    public void BuildTable_DuplicateCaseInsensitive_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DirectoryEntryTableWriter.BuildTable(
        [
            new DirectoryEntryTableWriter.DirectoryTableEntry("File.txt", false, 0, 0),
            new DirectoryEntryTableWriter.DirectoryTableEntry("file.txt", false, 0, 0),
        ]));
    }

    /// <summary>Verifies source attributes survive into the built tree.</summary>
    [Fact]
    public void BuildTable_PreservesAttributes()
    {
        AvlNode? root = DirectoryEntryTableWriter.BuildTable(
            [new DirectoryEntryTableWriter.DirectoryTableEntry("a.txt", false, 1, 2, Constants.AttributeRo)]);
        Assert.NotNull(root);
        Assert.Equal(Constants.AttributeRo, root.Attributes);
    }

    /// <summary>Verifies a null node is rejected when placing entries.</summary>
    [Fact]
    public void PlaceEntry_NullNode_Throws()
    {
        uint size = 0;
        Assert.Throws<ArgumentNullException>(() => DirectoryEntryTableWriter.PlaceEntry(null!, ref size));
    }

    /// <summary>Verifies offsets are DWORD-aligned and the running size advances.</summary>
    [Fact]
    public void PlaceEntry_DwordAligns()
    {
        AvlNode first = Node("a");
        AvlNode second = Node("bb");
        uint size = 0;

        DirectoryEntryTableWriter.PlaceEntry(first, ref size);
        DirectoryEntryTableWriter.PlaceEntry(second, ref size);

        Assert.Equal(0u, first.Offset);
        Assert.Equal(16u, second.Offset); // 14 + 1 char -> 15 -> 16
        Assert.Equal(32u, size); // 16 + (14 + 2 -> 16)
    }

    /// <summary>Verifies an entry that would straddle a sector boundary is moved to the next sector.</summary>
    [Fact]
    public void PlaceEntry_SectorStraddle_PadsToNextSector()
    {
        AvlNode node = Node("abc");
        uint size = Constants.SectorSize - 4; // 2044 + 20 bytes would straddle

        DirectoryEntryTableWriter.PlaceEntry(node, ref size);

        Assert.Equal((uint)Constants.SectorSize, node.Offset);
        Assert.Equal((uint)Constants.SectorSize + 20, size);
    }

    /// <summary>Verifies null and empty-subdirectory roots report one sector.</summary>
    [Fact]
    public void ComputeTableSize_EmptyRoots_ReturnSectorSize()
    {
        Assert.Equal((uint)Constants.SectorSize, DirectoryEntryTableWriter.ComputeTableSize(null));
        Assert.Equal((uint)Constants.SectorSize, DirectoryEntryTableWriter.ComputeTableSize(AvlNode.EmptySubdirectory));
    }

    /// <summary>Verifies a single-entry table size matches the encoded record size.</summary>
    [Fact]
    public void ComputeTableSize_SingleEntry()
    {
        AvlNode? root = DirectoryEntryTableWriter.BuildTable(
            [new DirectoryEntryTableWriter.DirectoryTableEntry("a", false, 0, 0)]);
        Assert.Equal(16u, DirectoryEntryTableWriter.ComputeTableSize(root));
    }

    /// <summary>Verifies the encoded header layout for a file entry.</summary>
    [Fact]
    public void EncodeEntry_FileHeaderLayout()
    {
        AvlNode node = Node("ab", isDirectory: false, startSector: 5, fileSize: 100);

        byte[] record = DirectoryEntryTableWriter.EncodeEntry(node);

        Assert.Equal(16, record.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2, 2)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8, 4)));
        Assert.Equal(Constants.AttributeArc, record[12]);
        Assert.Equal(2, record[13]);
        Assert.Equal("ab", System.Text.Encoding.Latin1.GetString(record, Constants.FilenameOffset, 2));
    }

    /// <summary>Verifies directory entries round their size up to a full sector and use directory attributes.</summary>
    [Fact]
    public void EncodeEntry_DirectoryRoundsSizeAndAttributes()
    {
        AvlNode node = Node("dir", isDirectory: true, fileSize: 100);

        byte[] record = DirectoryEntryTableWriter.EncodeEntry(node);

        Assert.Equal((uint)Constants.SectorSize, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8, 4)));
        Assert.Equal(Constants.AttributeDir, record[12]);
    }

    /// <summary>Verifies explicit attributes win over the directory/archive default.</summary>
    [Fact]
    public void EncodeEntry_ExplicitAttributesPreserved()
    {
        AvlNode node = Node("a.txt", attributes: Constants.AttributeRo | Constants.AttributeHid);
        byte[] record = DirectoryEntryTableWriter.EncodeEntry(node);
        Assert.Equal(Constants.AttributeRo | Constants.AttributeHid, record[12]);
    }

    /// <summary>Verifies child offsets are encoded in DWORD units.</summary>
    [Fact]
    public void EncodeEntry_ChildOffsetsInDwords()
    {
        AvlNode node = Node("a");
        node.Left = Node("l");
        node.Right = Node("r");
        node.Left.Offset = 64;
        node.Right.Offset = 128;

        byte[] record = DirectoryEntryTableWriter.EncodeEntry(node);

        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0, 2)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2, 2)));
    }

    /// <summary>Verifies names outside Latin-1 are rejected with a named error.</summary>
    [Fact]
    public void EncodeEntry_NonLatin1Name_Throws()
    {
        AvlNode node = Node("\u65E5\u672C");
        Assert.Throws<InvalidOperationException>(() => DirectoryEntryTableWriter.EncodeEntry(node));
    }

    /// <summary>Verifies an empty table serializes to one all-0xFF sector.</summary>
    [Fact]
    public void SerializeTable_Empty_AllFfSector()
    {
        byte[] buffer = DirectoryEntryTableWriter.SerializeTable(null);
        Assert.Equal(Constants.SectorSize, buffer.Length);
        Assert.All(buffer, b => Assert.Equal(Constants.PadByte, b));
    }

    /// <summary>Verifies a serialized table is sector-aligned, padded, and decodable.</summary>
    [Fact]
    public void SerializeTable_SingleEntry_IsPaddedAndDecodable()
    {
        AvlNode? root = DirectoryEntryTableWriter.BuildTable(
            [new DirectoryEntryTableWriter.DirectoryTableEntry("hello.bin", false, 42, 4096)]);
        Assert.NotNull(root);

        byte[] buffer = DirectoryEntryTableWriter.SerializeTable(root);

        Assert.Equal(0, buffer.Length % Constants.SectorSize);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4)));
        Assert.Equal(9, buffer[13]);
        for (int i = Constants.FilenameOffset + 9; i < buffer.Length; i++)
        {
            Assert.Equal(Constants.PadByte, buffer[i]);
        }
    }

    /// <summary>Verifies every record in a multi-entry table lands at its assigned offset.</summary>
    [Fact]
    public void SerializeTable_MultiEntry_RecordsAtOffsets()
    {
        AvlNode? root = DirectoryEntryTableWriter.BuildTable(
        [
            new DirectoryEntryTableWriter.DirectoryTableEntry("b.bin", false, 2, 200),
            new DirectoryEntryTableWriter.DirectoryTableEntry("a.bin", false, 1, 100),
            new DirectoryEntryTableWriter.DirectoryTableEntry("c.bin", false, 3, 300),
        ]);
        Assert.NotNull(root);

        byte[] buffer = DirectoryEntryTableWriter.SerializeTable(root);

        int seen = 0;
        AvlTree.AvlTraverseDepthFirst(root, (node, ctx, _) =>
        {
            byte[] record = DirectoryEntryTableWriter.EncodeEntry(node);
            byte[] actual = ((byte[])ctx!).AsSpan((int)node.Offset, record.Length).ToArray();
            Assert.Equal(record, actual);
            seen++;
            return 0;
        }, buffer, AvlTraversalMethod.Prefix, 0);
        Assert.Equal(3, seen);
    }
}
