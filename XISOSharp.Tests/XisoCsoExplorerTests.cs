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

    private string CreateIso()
    {
        var src = CreateTempDir("xiso_cso_src");
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");
        var outDir = CreateTempDir("xiso_cso_iso");
        var result = XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static string Compress(string iso, string csoName, long? splitBytes = null)
    {
        var cso = Path.Combine(Path.GetDirectoryName(iso)!, csoName);
        var rc = CisoWriter.CompressToCso(iso, cso, level: 0, splitBytes: splitBytes);
        Assert.Equal(0, rc);
        return cso;
    }

    [Fact]
    public void Open_SingleCso_VolumeValidAndRootLists()
    {
        var iso = CreateIso();
        var cso = Compress(iso, "game.cso");

        var explorer = new XisoExplorer(cso);

        Assert.True(explorer.Volume.IsValid);
        Assert.Equal(cso, explorer.IsoPath);
        var names = explorer.ListChildren("/").Select(n => n.Name + (n.IsDirectory ? "/" : "")).ToArray();
        Assert.Equal(new XisoExplorer(iso).ListChildren("/").Select(n => n.Name + (n.IsDirectory ? "/" : "")).ToArray(), names);
    }

    [Fact]
    public void Navigate_Subdirectory_MetadataMatchesIso()
    {
        var iso = CreateIso();
        var cso = Compress(iso, "game.cso");

        var fromCso = new XisoExplorer(cso);
        var fromIso = new XisoExplorer(iso);

        Assert.Equal(fromIso.ListChildren("/sub"), fromCso.ListChildren("/sub"));
        Assert.Equal(fromIso.GetNode("/sub/c.txt"), fromCso.GetNode("/sub/c.txt"));
        Assert.Equal(fromIso.GetNode("/b.txt"), fromCso.GetNode("/b.txt"));
    }

    [Fact]
    public void Hash_File_MatchesIsoDigest()
    {
        var iso = CreateIso();
        var cso = Compress(iso, "game.cso");

        var fromCso = new XisoExplorer(cso);
        var fromIso = new XisoExplorer(iso);

        Assert.Equal(fromIso.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256),
            fromCso.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256));
        Assert.Equal(fromIso.ComputeHashHex("/a.txt", HashAlgorithmName.MD5),
            fromCso.ComputeHashHex("/a.txt", HashAlgorithmName.MD5));
        Assert.Null(fromCso.ComputeHashHex("/missing.txt", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void CopyOut_FileAndDirectory_MatchIsoBytes()
    {
        var iso = CreateIso();
        var cso = Compress(iso, "game.cso");
        var work = CreateTempDir("xiso_cso_out");

        var fromCso = new XisoExplorer(cso);
        var fromIso = new XisoExplorer(iso);

        var csoFile = Path.Combine(work, "cso", "c.txt");
        var isoFile = Path.Combine(work, "iso", "c.txt");
        fromCso.CopyOut("/sub/c.txt", csoFile);
        fromIso.CopyOut("/sub/c.txt", isoFile);
        Assert.Equal(File.ReadAllBytes(isoFile), File.ReadAllBytes(csoFile));

        var csoDir = Path.Combine(work, "cso", "sub");
        var isoDir = Path.Combine(work, "iso", "sub");
        fromCso.CopyOut("/sub", csoDir);
        fromIso.CopyOut("/sub", isoDir);
        Assert.Equal(File.ReadAllBytes(Path.Combine(isoDir, "c.txt")),
            File.ReadAllBytes(Path.Combine(csoDir, "c.txt")));
    }

    [Fact]
    public void Open_SplitCso_FirstPartExplores()
    {
        var iso = CreateIso();
        // Force several parts: the fixture ISO is ~590 KB, so a 200 KiB split point parts it.
        var csoBase = Compress(iso, "game.cso", splitBytes: 200L * 1024);
        var first = Path.ChangeExtension(csoBase, ".1.cso");
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(Path.ChangeExtension(csoBase, ".2.cso")));

        var explorer = new XisoExplorer(first);

        Assert.True(explorer.Volume.IsValid);
        Assert.Equal(new XisoExplorer(iso).ListChildren("/"), explorer.ListChildren("/"));
        Assert.Equal(new XisoExplorer(iso).ComputeHashHex("/b.txt", HashAlgorithmName.SHA256),
            explorer.ComputeHashHex("/b.txt", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Open_GarbageCso_ThrowsXisoFormat()
    {
        var work = CreateTempDir("xiso_cso_bad");
        var bad = Path.Combine(work, "bad.cso");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Throws<XisoFormatException>(() => new XisoExplorer(bad));
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        var missing = Path.Combine(CreateTempDir("xiso_cso_miss"), "nope.cso");

        Assert.Throws<FileNotFoundException>(() => new XisoExplorer(missing));
    }
}
