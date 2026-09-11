using ZArchiveSharp;

namespace XISOSharp.Tests;

/// <summary>
/// on-disk XISO names are WINDOWS_1252 bytes and every reader path must
/// decode them via Latin1. <see cref="XisoRanges.CollectFileEntries"/> and
/// <see cref="XisoZarchive"/> previously used <c>Encoding.ASCII</c>, corrupting
/// bytes ≥ 0x80 into <c>'?'</c> (skeleton <c>.hash</c> paths, ZAR tree names).
/// Names below are Latin1-representable (U+0080–U+00FF); beyond-U+00FF names
/// cannot round-trip Latin1 by design.
/// </summary>
[Collection("Sequential")]
public sealed class XisoLatin1NamesTests : IDisposable
{
    private const string CafeFile = "caf\u00E9.txt";
    private const string NaiveFile = "na\u00EFve.dat";
    private const string UberDir = "sub-\u00FCber";
    private const string UberFile = UberDir + "/inner.txt";

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
        string src = CreateTempDir("xiso_latin_src");
        File.WriteAllText(Path.Combine(src, CafeFile), "caf\u00E9 content");
        File.WriteAllText(Path.Combine(src, NaiveFile), "na\u00EFve content");
        Directory.CreateDirectory(Path.Combine(src, UberDir));
        File.WriteAllText(Path.Combine(src, UberDir, "inner.txt"), "inner content");
        string outDir = CreateTempDir("xiso_latin_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void GetFileEntries_PreservesLatin1Names()
    {
        string iso = CreateIso();

        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(iso);
        HashSet<string> paths = entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(CafeFile, paths);
        Assert.Contains(NaiveFile, paths);
        Assert.Contains(UberFile, paths);
        Assert.DoesNotContain(paths, p => p.Contains('?'));
    }

    [Fact]
    public void Petrify_HashFile_PreservesLatin1Names()
    {
        string iso = CreateIso();
        string work = CreateTempDir("xiso_latin_hash");
        string skel = Path.Combine(work, "game.skeleton.xiso");
        string hash = Path.Combine(work, "game.hash");

        Assert.True(XisoSkeleton.Petrify(iso, skel, hash, 0, quiet: true));

        string[] lines = File.ReadAllLines(hash);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.EndsWith(" " + CafeFile, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(" " + NaiveFile, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(" " + UberFile, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains('?'));
    }

    [Fact]
    public void CreateZar_LookupAndExtract_PreserveLatin1Names()
    {
        string iso = CreateIso();
        string work = CreateTempDir("xiso_latin_zar");
        string zar = Path.Combine(work, "game.zar");

        Assert.True(XisoZarchive.CreateZar(iso, zar, 0, quiet: true));

        using ZArchiveReader? reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        foreach (string rel in new[] { CafeFile, NaiveFile, UberFile })
        {
            uint h = reader.LookUp(rel);
            Assert.NotEqual(ZArchiveReader.InvalidNode, h);
            Assert.True(reader.IsFile(h));
        }

        string outDir = Path.Combine(work, "out");
        ZArchiveTool.Extract(zar, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, CafeFile)));
        Assert.True(File.Exists(Path.Combine(outDir, NaiveFile)));
        Assert.True(File.Exists(Path.Combine(outDir, UberDir, "inner.txt")));
        Assert.Equal("caf\u00E9 content", File.ReadAllText(Path.Combine(outDir, CafeFile)));
    }
}
