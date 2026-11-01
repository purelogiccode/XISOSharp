using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// End-to-end tests for transparent split plain-ISO input
/// (<c>game.1.iso</c>, <c>game.2.iso</c>, …).
/// </summary>
[Collection("Sequential")]
public sealed class SplitIsoAutoDetectTests : IDisposable
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

    [Fact]
    public void Extract_SplitIsoInput_MatchesIsoExtraction()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);
        Assert.True(parts.Count >= 2);

        string outIso = Path.Combine(work, "from_iso");
        string outSplit = Path.Combine(work, "from_split");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(parts[0], outSplit, llCompat: false));
        AssertSameTree(outIso, outSplit);
    }

    [Fact]
    public void Extract_SplitIsoInput_DefaultDirUsesGameStem()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);
        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(work);
        try
        {
            Assert.Equal(0, XisoReader.Extract(parts[0], null, llCompat: false));
            Assert.True(Directory.Exists(Path.Combine(work, "game")));
            Assert.False(Directory.Exists(Path.Combine(work, "game.1")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(work, "game", "a.txt")));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void Checksum_SplitIsoMatchesIso()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);

        Assert.Equal(
            XisoChecksum.ComputeImageChecksum(iso),
            XisoChecksum.ComputeImageChecksum(parts[0]));
    }

    [Fact]
    public void Rewrite_SplitIso_ByteIdenticalToRewriteIso()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);
        string outIsoDir = Path.Combine(work, "rw_iso");
        string outSplitDir = Path.Combine(work, "rw_split");
        Directory.CreateDirectory(outIsoDir);
        Directory.CreateDirectory(outSplitDir);

        Assert.Equal(0, XisoReader.Rewrite(iso, outIsoDir, out string? isoOut));
        Assert.Equal(0, XisoReader.Rewrite(parts[0], outSplitDir, out string? splitOut));

        Assert.NotNull(isoOut);
        Assert.NotNull(splitOut);
        Assert.Equal("game.iso", Path.GetFileName(splitOut));
        Assert.Equal(File.ReadAllBytes(isoOut), File.ReadAllBytes(splitOut));
    }

    [Fact]
    public void ReaderVerbs_SplitIsoMatchIso()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);

        Assert.Equal(XisoReader.GetVolumeInfo(iso), XisoReader.GetVolumeInfo(parts[0]));
        Assert.Equal(XisoReader.IsOptimizedImage(iso), XisoReader.IsOptimizedImage(parts[0]));

        AuditResult isoAudit = XisoReader.AuditXiso(iso);
        AuditResult splitAudit = XisoReader.AuditXiso(parts[0]);
        Assert.Equal(isoAudit.IsValid, splitAudit.IsValid);
        Assert.Equal(isoAudit.FilesChecked, splitAudit.FilesChecked);
        Assert.Equal(isoAudit.DirsChecked, splitAudit.DirsChecked);

        SectorLayout isoLayout = XisoReader.GetSectorLayout(iso);
        SectorLayout splitLayout = XisoReader.GetSectorLayout(parts[0]);
        Assert.Equal(isoLayout.Entries.Count, splitLayout.Entries.Count);
        Assert.Equal(isoLayout.UsedRanges.Count, splitLayout.UsedRanges.Count);
        Assert.Equal(isoLayout.FreeRanges.Count, splitLayout.FreeRanges.Count);
        Assert.Equal(isoLayout.TotalSectors, splitLayout.TotalSectors);

        (List<(uint Start, uint End)> isoSys, List<(uint Start, uint End)> isoFiles) =
            XisoRanges.GetXisoRanges(iso);
        (List<(uint Start, uint End)> splitSys, List<(uint Start, uint End)> splitFiles) =
            XisoRanges.GetXisoRanges(parts[0]);
        Assert.Equal(isoSys, splitSys);
        Assert.Equal(isoFiles, splitFiles);
    }

    [Fact]
    public void RelativeSplitIsoPath_IsDetected()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        Split(iso, work, 256 * 1024);

        string splitDir = Path.Combine(work, "split");
        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(splitDir);
        try
        {
            Assert.True(XisoImageSource.TryDetect("game.1.iso", out XisoImageSourceInfo? info));
            Assert.NotNull(info);
            Assert.Equal(XisoImageFormat.SplitIso, info.Format);
            Assert.True(info.Parts.Count >= 2);
            Assert.Equal(0, XisoReader.List("game.1.iso", llCompat: false));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void MissingMiddlePart_ThrowsClearError()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 128 * 1024);
        Assert.True(parts.Count >= 3, "Test needs at least three parts.");

        File.Delete(parts[1]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => XisoImageSource.TryDetect(parts[0], out _));
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compress_SplitIsoToCci_MatchesIsoSource()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);
        string cci = Path.Combine(work, "from_split.cci");

        Assert.Equal(0, XisoCci.CompressToCci(parts[0], cci, splitBytes: 0));

        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, XisoCci.DecompressToIso(cci, extracted));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(extracted));
    }

    [Fact]
    public void Compress_SplitIsoToCiso_MatchesIsoSource()
    {
        string work = CreateTempDir("xiso_splitauto");
        string iso = CreateIso(work);
        IReadOnlyList<string> parts = Split(iso, work, 256 * 1024);
        string cso = Path.Combine(work, "from_split.cso");

        Assert.Equal(0, CisoWriter.CompressToCso(parts[0], cso, splitBytes: null));

        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(cso, extracted));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(extracted));
    }

    private static IReadOnlyList<string> Split(string iso, string work, long partSize)
    {
        string splitDir = Path.Combine(work, "split");
        Directory.CreateDirectory(splitDir);
        return XisoSplitter.Split(iso, Path.Combine(splitDir, "game.iso"), partSize);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateIso(string work)
    {
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static void AssertSameTree(string dirA, string dirB)
    {
        string[] filesA = Directory.GetFiles(dirA, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirA, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string[] filesB = Directory.GetFiles(dirB, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirB, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(filesA, filesB);
        foreach (string rel in filesA)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dirA, rel)), File.ReadAllBytes(Path.Combine(dirB, rel)));
    }
}
