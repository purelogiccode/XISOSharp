using XISOSharp.DataStructures;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="DirectoryEntryTableWriter"/> (TODO #3): AVL table builds,
/// offset/size computation, record encoding, and byte-identity with tables
/// written by the whole-image writer.
/// </summary>
[Collection("Sequential")]
public class DirectoryEntryTableWriterTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void BuildTable_Empty_ReturnsNull()
    {
        Assert.Null(DirectoryEntryTableWriter.BuildTable([]));
    }

    [Fact]
    public void BuildTable_InsertsAllEntries_SearchableByName()
    {
        var root = DirectoryEntryTableWriter.BuildTable([
            new DirectoryEntryTableWriter.DirectoryTableEntry("beta.txt", false, 10, 100),
            new DirectoryEntryTableWriter.DirectoryTableEntry("alpha.txt", false, 11, 200),
            new DirectoryEntryTableWriter.DirectoryTableEntry("GAMMA", true, 12, 2048),
        ]);

        Assert.NotNull(root);
        Assert.Equal("beta.txt", AvlTree.AvlFetch(root, "BETA.txt")!.Filename);
        Assert.Equal("alpha.txt", AvlTree.AvlFetch(root, "Alpha.TXT")!.Filename);
        Assert.NotNull(AvlTree.AvlFetch(root, "gamma")!.Subdirectory);
        Assert.Null(AvlTree.AvlFetch(root, "beta.txt")!.Subdirectory);
        Assert.Null(AvlTree.AvlFetch(root, "missing.txt"));
    }

    [Fact]
    public void BuildTable_DuplicateCaseInsensitive_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DirectoryEntryTableWriter.BuildTable([
                new DirectoryEntryTableWriter.DirectoryTableEntry("File.txt", false, 10, 100),
                new DirectoryEntryTableWriter.DirectoryTableEntry("FILE.TXT", false, 11, 100),
            ]));
        Assert.Contains("File.txt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("")]
    public void BuildTable_InvalidNames_Throw(string name)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DirectoryEntryTableWriter.BuildTable(
                [new DirectoryEntryTableWriter.DirectoryTableEntry(name, false, 10, 100)]));
    }

    [Fact]
    public void BuildTable_TooLongName_Throws()
    {
        var name = new string('a', Constants.FilenameMaxChars + 1);
        Assert.Throws<InvalidOperationException>(() =>
            DirectoryEntryTableWriter.BuildTable(
                [new DirectoryEntryTableWriter.DirectoryTableEntry(name, false, 10, 100)]));
    }

    [Fact]
    public void ComputeTableSize_SingleFile_MatchesHandComputed()
    {
        // "a": 14 + 1 = 15 bytes, DWORD-padded to 16.
        var root = DirectoryEntryTableWriter.BuildTable(
            [new DirectoryEntryTableWriter.DirectoryTableEntry("a", false, 7, 5)]);
        Assert.Equal(16u, DirectoryEntryTableWriter.ComputeTableSize(root));
        Assert.Equal(0u, root!.Offset);
    }

    [Fact]
    public void ComputeTableSize_AvoidsSectorStraddle()
    {
        // 8 entries x (14 + 255 = 269 -> 272 bytes): the 8th would span
        // 1904..2176 across the 2048 boundary, so it moves to offset 2048
        // and the total is 2048 + 272 = 2320.
        var entries = Enumerable.Range(0, 8).Select(i =>
            new DirectoryEntryTableWriter.DirectoryTableEntry($"f{i:D3}_{new string('x', 250)}", false, (uint)i, 10));
        var root = DirectoryEntryTableWriter.BuildTable(entries);
        Assert.Equal(2320u, DirectoryEntryTableWriter.ComputeTableSize(root));

        var max = new uint[1];
        AvlTree.AvlTraverseDepthFirst(root, static (node, ctx, _) =>
        {
            var seen = (uint[])ctx!;
            if (node.Offset > seen[0]) seen[0] = node.Offset;
            return 0;
        }, max, AvlTraversalMethod.Prefix, 0);
        Assert.Equal(2048u, max[0]);
    }

    [Fact]
    public void SerializeTable_Empty_ReturnsSingleFFSector()
    {
        foreach (var empty in new[] { null, AvlNode.EmptySubdirectory })
        {
            var bytes = DirectoryEntryTableWriter.SerializeTable(empty);
            Assert.Equal(Constants.SectorSize, bytes.Length);
            Assert.All(bytes, static b => Assert.Equal(Constants.PadByte, b));
        }

        Assert.Equal((uint)Constants.SectorSize, DirectoryEntryTableWriter.ComputeTableSize(null));
    }

    [Fact]
    public void EncodeEntry_FileRecord_MatchesHandComputedBytes()
    {
        var node = new AvlNode { Filename = "AB", StartSector = 0x123, FileSize = 0x456 };
        var record = DirectoryEntryTableWriter.EncodeEntry(node);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, // lOffset: no left child
            0x00, 0x00, // rOffset: no right child
            0x23, 0x01, 0x00, 0x00, // StartSector
            0x56, 0x04, 0x00, 0x00, // FileSize (exact for files)
            0x20, // AttributeArc
            0x02, // name length
            0x41, 0x42, // "AB"
        }, record);
    }

    [Fact]
    public void EncodeEntry_DirectoryRecord_RoundsSizeAndSetsDirAttribute()
    {
        // Table byte size 100 -> on-disk 2048; child offsets stored in DWORDs.
        var root = new AvlNode
        {
            Filename = "SUB",
            Subdirectory = new AvlNode(),
            StartSector = 0x200,
            FileSize = 100,
            Left = new AvlNode { Filename = "a", Offset = 40 },
            Right = new AvlNode { Filename = "z", Offset = 80 },
        };
        var record = DirectoryEntryTableWriter.EncodeEntry(root);
        Assert.Equal(new byte[]
        {
            0x0A, 0x00, // lOffset: 40 / 4
            0x14, 0x00, // rOffset: 80 / 4
            0x00, 0x02, 0x00, 0x00, // StartSector
            0x00, 0x08, 0x00, 0x00, // FileSize rounded to sector
            0x10, // AttributeDir
            0x03, // name length
            0x53, 0x55, 0x42, // "SUB"
        }, record);
    }

    [Fact]
    public void SerializeTable_MatchesWriterOutput_ForEveryTableInImage()
    {
        var src = CreateTempDir("xiso_tbl_src");
        File.WriteAllText(Path.Combine(src, "file1.txt"), "hello");
        var bin = new byte[5000];
        new Random(42).NextBytes(bin);
        File.WriteAllBytes(Path.Combine(src, "file2.txt"), bin);
        File.WriteAllBytes(Path.Combine(src, "empty.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(src, "subdir"));
        File.WriteAllText(Path.Combine(src, "subdir", "nested.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(src, "emptydir"));

        var outDir = CreateTempDir("xiso_tbl_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null));
        Assert.NotNull(isoPath);

        var layout = XisoReader.GetSectorLayout(isoPath);
        var dirSizes = layout.Entries.Where(static e => e.IsDirectory)
            .ToDictionary(static e => e.Path, static e => e.FileSize, StringComparer.Ordinal);

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var dir in layout.Entries.Where(static e => e.IsDirectory))
        {
            var entries = XisoReader.ListDirectory(isoPath, dir.Path)
                .Select(e => new DirectoryEntryTableWriter.DirectoryTableEntry(
                    e.Name,
                    e.IsDirectory,
                    e.StartSector,
                    e.IsDirectory ? dirSizes[JoinPath(dir.Path, e.Name)] : e.FileSize))
                .ToList();

            var table = DirectoryEntryTableWriter.BuildTable(entries);
            var serialized = DirectoryEntryTableWriter.SerializeTable(table);

            var onDisk = new byte[dir.SectorCount * Constants.SectorSize];
            fs.Seek(layout.Volume.DiscLseek + ((long)dir.StartSector * Constants.SectorSize), SeekOrigin.Begin);
            var read = 0;
            while (read < onDisk.Length)
            {
                var n = fs.Read(onDisk, read, onDisk.Length - read);
                Assert.True(n > 0, "Truncated directory table on disk.");
                read += n;
            }

            Assert.Equal(onDisk, serialized);
        }
    }

    private static string JoinPath(string dir, string name)
    {
        return dir.Equals("/", StringComparison.Ordinal) ? "/" + name : dir + "/" + name;
    }
}
