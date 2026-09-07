using System.Buffers.Binary;
using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoExplorer"/> — the UI-agnostic engine behind the
/// WPF Tester's Explore tab (TODO #11, xdvdfs #120): load, navigate, copy-out,
/// hash, XEX, and path helpers.
/// </summary>
[Collection("Sequential")]
public class XisoExplorerTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static byte[] BuildXex2()
    {
        byte[] data = new byte[0x1000];
        Span<byte> span = data.AsSpan();

        "XEX2"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x04..], 0x89);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x08..], 0x400);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x10..], 0x300);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x14..], 4);

        BinaryPrimitives.WriteUInt32BigEndian(span[0x18..], 0x00010100);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x1C..], 0x12345678); // entry point
        BinaryPrimitives.WriteUInt32BigEndian(span[0x20..], 0x00010201);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x24..], 0x82000000); // image base
        BinaryPrimitives.WriteUInt32BigEndian(span[0x28..], 0x00040006);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x2C..], 0x200); // execution info
        BinaryPrimitives.WriteUInt32BigEndian(span[0x30..], 0x000003FF);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x34..], 0x218); // file format info

        BinaryPrimitives.WriteUInt32BigEndian(span[0x200..], 0x2B35C136); // media id
        BinaryPrimitives.WriteUInt32BigEndian(span[0x204..], 0x00000002); // version
        BinaryPrimitives.WriteUInt32BigEndian(span[0x208..], 0x00000002); // base version
        BinaryPrimitives.WriteUInt32BigEndian(span[0x20C..], 0x4D5307D3); // title id
        data[0x210] = 0x00;
        data[0x211] = 0x00;
        data[0x212] = 0x01; // disc number
        data[0x213] = 0x02; // disc count
        BinaryPrimitives.WriteUInt32BigEndian(span[0x214..], 0);

        BinaryPrimitives.WriteUInt32BigEndian(span[0x218..], 0x24);
        BinaryPrimitives.WriteUInt16BigEndian(span[0x21C..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[0x21E..], 2);

        BinaryPrimitives.WriteUInt32BigEndian(span[0x300..], 0x1F3C);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x304..], 0x013D0000);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x30C..], 0x8);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x410..], 0x82000000);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x478..], 0xFD00);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x47C..], 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x480..], 0);

        return data;
    }

    private string CreateExplorerIso()
    {
        string srcDir = CreateTempDir("xiso_explore_src");
        File.WriteAllText(Path.Combine(srcDir, "readme.txt"), "hello explorer");
        File.WriteAllBytes(Path.Combine(srcDir, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(srcDir, "default.xex"), BuildXex2());
        string sub = Path.Combine(srcDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "nested.bin"), [1, 2, 3, 4, 5]);

        string outputDir = CreateTempDir("xiso_explore_out");
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void Open_ValidImage_VolumeValidAndRootChildren()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        Assert.True(explorer.Volume.IsValid);
        Assert.True(explorer.Volume.FileLength > 0);

        HashSet<string> names = explorer.ListChildren("/").Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("readme.txt", names);
        Assert.Contains("empty.bin", names);
        Assert.Contains("default.xex", names);
        Assert.Contains("sub", names);

        ExplorerNode? root = explorer.GetNode("/");
        Assert.NotNull(root);
        Assert.True(root.IsDirectory);
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(CreateTempDir("xiso_explore"), "nope.iso");
        Assert.Throws<FileNotFoundException>(() => new XisoExplorer(missing));
    }

    [Fact]
    public void Open_GarbageFile_ThrowsXisoFormat()
    {
        string dir = CreateTempDir("xiso_explore");
        string garbage = Path.Combine(dir, "garbage.iso");
        File.WriteAllBytes(garbage, new byte[65536]);
        Assert.Throws<XisoFormatException>(() => new XisoExplorer(garbage));
    }

    [Fact]
    public void Open_EmptyPath_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => new XisoExplorer(string.Empty));
        Assert.Throws<ArgumentException>(() => new XisoExplorer(null!));
    }

    [Fact]
    public void ListChildren_Subdirectory_CaseInsensitive()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        IReadOnlyList<ExplorerNode> children = explorer.ListChildren("/SUB");
        ExplorerNode nested = Assert.Single(children);
        Assert.Equal("nested.bin", nested.Name);
        Assert.False(nested.IsDirectory);
        Assert.Equal(5, nested.Size);
        Assert.Equal("/SUB/nested.bin", nested.FullPath);
    }

    [Fact]
    public void ListChildren_Missing_ThrowsInvalidData()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/nope"));
    }

    [Fact]
    public void ListChildren_FilePath_ThrowsInvalidData()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/readme.txt"));
    }

    [Fact]
    public void GetNode_File_ReturnsMetadata()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        ExplorerNode? node = explorer.GetNode("/readme.txt");
        Assert.NotNull(node);
        Assert.Equal("readme.txt", node.Name);
        Assert.False(node.IsDirectory);
        Assert.Equal("hello explorer".Length, node.Size);
    }

    [Fact]
    public void GetNode_Missing_ReturnsNull()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        Assert.Null(explorer.GetNode("/nope.txt"));
    }

    [Fact]
    public void Combine_And_Normalize()
    {
        Assert.Equal("/a", XisoExplorer.Combine("/", "a"));
        Assert.Equal("/sub/f", XisoExplorer.Combine("/sub", "f"));
        Assert.Equal("/sub/f", XisoExplorer.Combine("/sub/", "f"));

        Assert.Equal("/", XisoExplorer.Normalize(""));
        Assert.Equal("/", XisoExplorer.Normalize(null));
        Assert.Equal("/sub/dir", XisoExplorer.Normalize("sub\\dir/"));
        Assert.Equal("/sub", XisoExplorer.Normalize("/sub/"));
    }

    [Fact]
    public void CopyOut_File_BytesEqual()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        string dest = Path.Combine(CreateTempDir("xiso_explore"), "readme.txt");

        explorer.CopyOut("/readme.txt", dest);

        Assert.Equal("hello explorer", File.ReadAllText(dest));
    }

    [Fact]
    public void CopyOut_Directory_Recursive()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        string dest = Path.Combine(CreateTempDir("xiso_explore"), "sub_out");

        explorer.CopyOut("/sub", dest);

        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(dest, "nested.bin")));
    }

    [Fact]
    public void CopyOut_Missing_ThrowsInvalidData()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        string dest = Path.Combine(CreateTempDir("xiso_explore"), "nope.txt");
        Assert.Throws<InvalidDataException>(() => explorer.CopyOut("/nope.txt", dest));
    }

    [Fact]
    public void Hash_File_MatchesDirectCompute()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        string? hex = explorer.ComputeHashHex("/readme.txt", HashAlgorithmName.SHA256);
        string expected = Convert.ToHexString(
            XisoReader.ComputeFileHash(explorer.IsoPath, "/readme.txt", HashAlgorithmName.SHA256)!);

        Assert.Equal(expected, hex);
        Assert.Equal(64, hex!.Length);
    }

    [Fact]
    public void Hash_EmptyFile_KnownDigest()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        string? hex = explorer.ComputeHashHex("/empty.bin", HashAlgorithmName.SHA256);

        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hex);
    }

    [Fact]
    public void Hash_Missing_ReturnsNull()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        Assert.Null(explorer.ComputeHashHex("/nope.txt", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Hash_Directory_ThrowsInvalidData()
    {
        XisoExplorer explorer = new(CreateExplorerIso());
        Assert.Throws<InvalidDataException>(() => explorer.ComputeHashHex("/sub", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Xex_RealXex_ParsesFields()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        XexInfo? xex = explorer.GetXexInfo("/default.xex");

        Assert.NotNull(xex);
        Assert.Equal(0x89u, xex.ModuleFlags);
        Assert.Equal(0x12345678u, xex.EntryPoint);
        Assert.Equal(0x4D5307D3u, xex.TitleId);
    }

    [Fact]
    public void Xex_NonXex_ReturnsNull()
    {
        XisoExplorer explorer = new(CreateExplorerIso());

        Assert.Null(explorer.GetXexInfo("/readme.txt"));
        Assert.Null(explorer.GetXexInfo("/sub"));
        Assert.Null(explorer.GetXexInfo("/nope.xex"));
    }
}
