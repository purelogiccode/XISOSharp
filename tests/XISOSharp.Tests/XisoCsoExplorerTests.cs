using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// <see cref="XisoExplorer"/> over CISO containers — single
/// <c>.cso</c> and split <c>.1.cso</c> part sets must explore identically to
/// the source ISO (navigate, metadata, hash, copy-out), and garbage containers
/// must fail under the documented contract.
/// </summary>
[Collection("Sequential")]
public sealed class XisoCsoExplorerTests : IDisposable
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
                // ignored
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

    private string CreateIso()
    {
        string src = CreateTempDir("xiso_cso_src");
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");
        string outDir = CreateTempDir("xiso_cso_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static string Compress(string iso, string csoName, long? splitBytes = null)
    {
        string cso = Path.Combine(Path.GetDirectoryName(iso)!, csoName);
        int rc = CisoWriter.CompressToCso(iso, cso, level: 0, splitBytes: splitBytes);
        Assert.Equal(0, rc);
        return cso;
    }

    [Fact]
    public void Open_SingleCso_VolumeValidAndRootLists()
    {
        string iso = CreateIso();
        string cso = Compress(iso, "game.cso");

        XisoExplorer explorer = new(cso);

        Assert.True(explorer.Volume.IsValid);
        Assert.Equal(cso, explorer.IsoPath);
        string[] names = explorer.ListChildren("/").Select(n => n.Name + (n.IsDirectory ? "/" : "")).ToArray();
        Assert.Equal(new XisoExplorer(iso).ListChildren("/").Select(n => n.Name + (n.IsDirectory ? "/" : "")).ToArray(),
            names);
    }

    [Fact]
    public void Navigate_Subdirectory_MetadataMatchesIso()
    {
        string iso = CreateIso();
        string cso = Compress(iso, "game.cso");

        XisoExplorer fromCso = new(cso);
        XisoExplorer fromIso = new(iso);

        Assert.Equal(fromIso.ListChildren("/sub"), fromCso.ListChildren("/sub"));
        Assert.Equal(fromIso.GetNode("/sub/c.txt"), fromCso.GetNode("/sub/c.txt"));
        Assert.Equal(fromIso.GetNode("/b.txt"), fromCso.GetNode("/b.txt"));
    }

    [Fact]
    public void Hash_File_MatchesIsoDigest()
    {
        string iso = CreateIso();
        string cso = Compress(iso, "game.cso");

        XisoExplorer fromCso = new(cso);
        XisoExplorer fromIso = new(iso);

        Assert.Equal(fromIso.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256),
            fromCso.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256));
        Assert.Equal(fromIso.ComputeHashHex("/a.txt", HashAlgorithmName.MD5),
            fromCso.ComputeHashHex("/a.txt", HashAlgorithmName.MD5));
        Assert.Null(fromCso.ComputeHashHex("/missing.txt", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void CopyOut_FileAndDirectory_MatchIsoBytes()
    {
        string iso = CreateIso();
        string cso = Compress(iso, "game.cso");
        string work = CreateTempDir("xiso_cso_out");

        XisoExplorer fromCso = new(cso);
        XisoExplorer fromIso = new(iso);

        string csoFile = Path.Combine(work, "cso", "c.txt");
        string isoFile = Path.Combine(work, "iso", "c.txt");
        fromCso.CopyOut("/sub/c.txt", csoFile);
        fromIso.CopyOut("/sub/c.txt", isoFile);
        Assert.Equal(File.ReadAllBytes(isoFile), File.ReadAllBytes(csoFile));

        string csoDir = Path.Combine(work, "cso", "sub");
        string isoDir = Path.Combine(work, "iso", "sub");
        fromCso.CopyOut("/sub", csoDir);
        fromIso.CopyOut("/sub", isoDir);
        Assert.Equal(File.ReadAllBytes(Path.Combine(isoDir, "c.txt")),
            File.ReadAllBytes(Path.Combine(csoDir, "c.txt")));
    }

    [Fact]
    public void Open_SplitCso_FirstPartExplores()
    {
        string iso = CreateIso();
        // Force several parts: the fixture ISO is ~590 KB, so a 200 KiB split point parts it.
        string csoBase = Compress(iso, "game.cso", splitBytes: 200L * 1024);
        string first = Path.ChangeExtension(csoBase, ".1.cso");
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(Path.ChangeExtension(csoBase, ".2.cso")));

        XisoExplorer explorer = new(first);

        Assert.True(explorer.Volume.IsValid);
        Assert.Equal(new XisoExplorer(iso).ListChildren("/"), explorer.ListChildren("/"));
        Assert.Equal(new XisoExplorer(iso).ComputeHashHex("/b.txt", HashAlgorithmName.SHA256),
            explorer.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Open_GarbageCso_ThrowsXisoFormat()
    {
        string work = CreateTempDir("xiso_cso_bad");
        string bad = Path.Combine(work, "bad.cso");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Throws<XisoFormatException>(() => new XisoExplorer(bad));
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(CreateTempDir("xiso_cso_miss"), "nope.cso");

        Assert.Throws<FileNotFoundException>(() => new XisoExplorer(missing));
    }
}
