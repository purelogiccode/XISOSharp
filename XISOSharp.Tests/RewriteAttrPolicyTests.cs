using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Pins the rewrite attribute policy (extract-xiso byte parity):
/// by default a rewrite re-encodes every dirent with the DIR/ARC defaults
/// (dir=0x10, file=0x20), matching extract-xiso v2.7.1, which discards the
/// source attribute byte entirely (extract-xiso.c:2252). Opt-in
/// <c>preserveAttributes: true</c> restores BUG-LIB-034 fidelity.
/// </summary>
[Collection("Sequential")]
public class RewriteAttrPolicyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Builds a small ISO, then flips one file's on-disk dirent attribute
    /// byte to 0x80 (NOR) — the value real dumps carry that extract-xiso drops.</summary>
    private string CreateIsoWithNorFile()
    {
        string src = CreateTempDir();
        string outDir = CreateTempDir();
        File.WriteAllText(Path.Combine(src, "file.txt"), "attribute parity");
        File.WriteAllText(Path.Combine(src, "other.txt"), "second entry");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);

        SetDirentAttribute(isoPath, "file.txt", 0x80);
        Assert.Equal(0x80, GetDirentAttribute(isoPath, "file.txt"));
        return isoPath;
    }

    [Fact]
    public void Rewrite_Default_NormalizesFileAttributesToArchive()
    {
        string isoPath = CreateIsoWithNorFile();
        string outDir = CreateTempDir();

        Assert.Equal(0, XisoReader.Rewrite(isoPath, outDir, out string? rewrittenPath));
        Assert.NotNull(rewrittenPath);

        // extract-xiso parity: the writer re-encodes files with AttributeArc (0x20)
        // regardless of the source byte.
        Assert.Equal(Constants.AttributeArc, GetDirentAttribute(rewrittenPath, "file.txt"));
    }

    [Fact]
    public void Rewrite_PreserveAttributes_KeepsSourceBits()
    {
        string isoPath = CreateIsoWithNorFile();
        string outDir = CreateTempDir();

        Assert.Equal(0, XisoReader.Rewrite(isoPath, outDir, out string? rewrittenPath,
            preserveAttributes: true));
        Assert.NotNull(rewrittenPath);

        // BUG-LIB-034 opt-in: the source NOR byte survives the rewrite.
        Assert.Equal(0x80, GetDirentAttribute(rewrittenPath, "file.txt"));
    }

    [Fact]
    public void Rewrite_Default_KeepsDirectoryAttributes()
    {
        // Both policies encode dirs as 0x10; the normalization must not touch them.
        string src = CreateTempDir();
        string outDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "nested.txt"), "x");
        string buildDir = CreateTempDir();
        Assert.Equal(0, XisoWriter.CreateXiso(src, buildDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);

        SetDirentAttribute(isoPath, "sub", 0x13);
        Assert.Equal(0, XisoReader.Rewrite(isoPath, outDir, out string? rewrittenPath));
        Assert.NotNull(rewrittenPath);
        Assert.Equal(Constants.AttributeDir, GetDirentAttribute(rewrittenPath, "sub"));
    }

    private static byte GetDirentAttribute(string isoPath, string filename)
    {
        byte[] dirTable = ReadRootDirTable(isoPath);
        int idx = FindDirent(dirTable, filename);
        return dirTable[idx - 2];
    }

    private static void SetDirentAttribute(string isoPath, string filename, byte value)
    {
        SectorLayout layout = XisoReader.GetSectorLayout(isoPath);
        FileSectorExtent root = layout.Entries.First(static e => e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        byte[] dirTable = ReadDirTable(isoPath, root, layout.Volume.DiscLseek);
        int idx = FindDirent(dirTable, filename);
        dirTable[idx - 2] = value;
        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(layout.Volume.DiscLseek + ((long)root.StartSector * Constants.SectorSize), SeekOrigin.Begin);
        fs.Write(dirTable, 0, dirTable.Length);
    }

    private static byte[] ReadRootDirTable(string isoPath)
    {
        SectorLayout layout = XisoReader.GetSectorLayout(isoPath);
        FileSectorExtent root = layout.Entries.First(static e => e.IsDirectory && string.Equals(e.Path, "/", StringComparison.OrdinalIgnoreCase));
        return ReadDirTable(isoPath, root, layout.Volume.DiscLseek);
    }

    private static byte[] ReadDirTable(string isoPath, FileSectorExtent dir, long discLseek)
    {
        byte[] table = new byte[dir.SectorCount * Constants.SectorSize];
        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(discLseek + ((long)dir.StartSector * Constants.SectorSize), SeekOrigin.Begin);
        int read = 0;
        while (read < table.Length)
        {
            int n = fs.Read(table, read, table.Length - read);
            Assert.True(n > 0, "Truncated directory table on disk.");
            read += n;
        }

        return table;
    }

    /// <summary>Finds the dirent's filename start inside a raw dir table: the byte
    /// before the name is the name length, the byte before that is the attribute.</summary>
    private static int FindDirent(byte[] table, string filename)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(filename);
        for (int i = 0; i <= table.Length - needle.Length; i++)
        {
            bool ok = table[i] == needle.Length;
            for (int j = 0; ok && j < needle.Length; j++)
            {
                ok = table[i + 1 + j] == needle[j];
            }

            if (ok)
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"Dirent '{filename}' not found in directory table.");
    }
}
