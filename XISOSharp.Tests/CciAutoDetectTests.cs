using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// End-to-end tests for transparent CCI input across the read verbs:
/// list, tree, extract, unpack, rewrite, checksum and the in-place guards.
/// </summary>
[Collection("Sequential")]
public sealed class CciAutoDetectTests : IDisposable
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
    public void Extract_CciInput_MatchesIsoExtraction()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string outIso = Path.Combine(work, "from_iso");
        string outCci = Path.Combine(work, "from_cci");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(cci, outCci, llCompat: false));
        AssertSameTree(outIso, outCci);
    }

    [Fact]
    public void Extract_SplitCciInput_MatchesIsoExtraction()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 1 << 20);
        string firstPart = Path.Combine(work, "game.1.cci");
        Assert.True(File.Exists(firstPart));

        string outIso = Path.Combine(work, "from_iso");
        string outCci = Path.Combine(work, "from_split");

        Assert.Equal(0, XisoReader.Extract(iso, outIso, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(firstPart, outCci, llCompat: false));
        AssertSameTree(outIso, outCci);
    }

    [Fact]
    public void Extract_CciInput_DefaultDirUsesGameStem()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(work);
        try
        {
            Assert.Equal(0, XisoReader.Extract(cci, null, llCompat: false));
            Assert.True(Directory.Exists(Path.Combine(work, "game")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(work, "game", "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(work, "game.cci")));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void ListAndTree_CciInput_SucceedAndNameContainer()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);

        string listCciLog, treeCciLog, listIsoLog;
        using (LogCapture capture = new())
        {
            Assert.Equal(0, XisoReader.List(cci, llCompat: false));
            listCciLog = capture.Output;
        }

        using (LogCapture capture = new())
        {
            Assert.Equal(0, XisoReader.Tree(cci, llCompat: false));
            treeCciLog = capture.Output;
        }

        using (LogCapture capture = new())
        {
            Assert.Equal(0, XisoReader.List(iso, llCompat: false));
            listIsoLog = capture.Output;
        }

        Assert.Contains("listing game.cci", listCciLog, StringComparison.Ordinal);
        Assert.Contains("a.txt", treeCciLog, StringComparison.Ordinal);
        Assert.Equal(
            listIsoLog.Replace("game.iso", "IMG", StringComparison.Ordinal),
            listCciLog.Replace("game.cci", "IMG", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpackImage_CciInput_MatchesIsoUnpack()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string outIso = Path.Combine(work, "from_iso");
        string outCci = Path.Combine(work, "from_cci");

        Assert.Equal(0, XisoReader.UnpackImage(iso, outIso));
        Assert.Equal(0, XisoReader.UnpackImage(cci, outCci));
        AssertSameTree(outIso, outCci);
    }

    [Fact]
    public void Rewrite_CciInput_ByteIdenticalToRewriteIso()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string outIsoDir = Path.Combine(work, "rw_iso");
        string outCciDir = Path.Combine(work, "rw_cci");
        Directory.CreateDirectory(outIsoDir);
        Directory.CreateDirectory(outCciDir);

        Assert.Equal(0, XisoReader.Rewrite(iso, outIsoDir, out string? isoOut));
        Assert.Equal(0, XisoReader.Rewrite(cci, outCciDir, out string? cciOut));

        Assert.NotNull(isoOut);
        Assert.NotNull(cciOut);
        Assert.Equal("game.iso", Path.GetFileName(cciOut));
        Assert.Equal(File.ReadAllBytes(isoOut), File.ReadAllBytes(cciOut));
    }

    [Fact]
    public void Checksum_CciMatchesIso()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);

        Assert.Equal(
            XisoChecksum.ComputeImageChecksum(iso),
            XisoChecksum.ComputeImageChecksum(cci));
    }

    [Fact]
    public void ReaderVerbs_CciMatchIso()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);

        Assert.Equal(XisoReader.GetVolumeInfo(iso), XisoReader.GetVolumeInfo(cci));
        Assert.Equal(XisoReader.GetFileTimeRaw(iso), XisoReader.GetFileTimeRaw(cci));
        Assert.Equal(XisoReader.IsOptimizedImage(iso), XisoReader.IsOptimizedImage(cci));

        SectorLayout isoLayout = XisoReader.GetSectorLayout(iso);
        SectorLayout cciLayout = XisoReader.GetSectorLayout(cci);
        Assert.Equal(isoLayout.Entries.Count, cciLayout.Entries.Count);
        Assert.Equal(isoLayout.UsedRanges.Count, cciLayout.UsedRanges.Count);
        Assert.Equal(isoLayout.FreeRanges.Count, cciLayout.FreeRanges.Count);
        Assert.Equal(isoLayout.TotalSectors, cciLayout.TotalSectors);
    }

    [Fact]
    public void SetFileTime_RefusesCci()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);

        IOException exception = Assert.Throws<IOException>(
            () => XisoReader.SetFileTime(cci, DateTimeOffset.UtcNow));
        Assert.Contains("compressed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repair_RefusesCci()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => XisoReader.Repair(cci, createBackup: false));
        Assert.Contains("compressed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyIntoImage_RefusesCci()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string host = Path.Combine(work, "host.bin");
        File.WriteAllBytes(host, [1, 2, 3]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => XisoPatcher.CopyIntoImage(cci, host, "/host.bin"));
        Assert.Contains("compressed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecompressToIso_Cci_MatchesSource()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string extracted = Path.Combine(work, "extracted.iso");

        Assert.Equal(0, XisoCci.DecompressToIso(cci, extracted));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(extracted));

        Assert.Equal(0, XisoCci.DecompressToIso(cci, extracted, overwrite: true));
    }

    [Fact]
    public void RelativeSplitCciPath_IsDetected()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 100_000);

        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(work);
        try
        {
            Assert.True(XisoImageSource.TryDetect("game.1.cci", out XisoImageSourceInfo? info));
            Assert.NotNull(info);
            Assert.Equal(XisoImageFormat.SplitCci, info.Format);
            Assert.Equal(0, XisoReader.List("game.1.cci", llCompat: false));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void MissingMiddleCciPart_ThrowsClearError()
    {
        string work = CreateTempDir("xiso_cciauto");
        string src = Path.Combine(work, "src_random");
        Directory.CreateDirectory(src);
        byte[] random = new byte[400_000];
        Random.Shared.NextBytes(random);
        File.WriteAllBytes(Path.Combine(src, "r.bin"), random);
        string isoDir = Path.Combine(work, "iso_random");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? iso, "game.iso", null));
        Assert.NotNull(iso);

        Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 100_000);

        string first = Path.Combine(work, "game.1.cci");
        string second = Path.Combine(work, "game.2.cci");
        string third = Path.Combine(work, "game.3.cci");
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
        File.Delete(second);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => XisoImageSource.TryDetect(first, out _));
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompressDirectory_ToCci_MatchesSourceTree()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string source = Path.Combine(work, "src");
        string cci = Path.Combine(work, "dir.cci");
        string extractedIso = Path.Combine(work, "dir.iso");
        string extractedFiles = Path.Combine(work, "dir_files");

        Assert.Equal(0, XisoCci.CompressToCci(source, cci, splitBytes: 0));
        Assert.Equal(0, XisoCci.DecompressToIso(cci, extractedIso));
        Assert.Equal(0, XisoReader.Extract(extractedIso, extractedFiles, llCompat: false));
        AssertSameTree(source, extractedFiles);
        Assert.True(File.Exists(iso));
    }

    [Fact]
    public void CompressCiso_ToCci_RoundTrips()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cso = Path.Combine(work, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, splitBytes: null));

        string cci = Path.Combine(work, "from_cso.cci");
        Assert.Equal(0, XisoCci.CompressToCci(cso, cci, splitBytes: 0));

        string extracted = Path.Combine(work, "from_cso.iso");
        Assert.Equal(0, XisoCci.DecompressToIso(cci, extracted));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(extracted));
    }

    [Fact]
    public void CompressCci_ToCiso_RoundTrips()
    {
        string work = CreateTempDir("xiso_cciauto");
        string iso = CreateIso(work);
        string cci = Compress(iso, Path.Combine(work, "game.cci"), splitBytes: 0);
        string cso = Path.Combine(work, "from_cci.cso");

        Assert.Equal(0, CisoWriter.CompressToCso(cci, cso, splitBytes: null));

        string extracted = Path.Combine(work, "from_cci.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(cso, extracted));
        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(extracted));
    }

    private static string Compress(string iso, string cciPath, long splitBytes)
    {
        Assert.Equal(0, XisoCci.CompressToCci(iso, cciPath, splitBytes: splitBytes));
        return cciPath;
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

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _origOut = Logger.Out;
        private readonly TextWriter _origErr = Logger.Error;
        private readonly bool _origQuiet = Logger.Quiet;
        private readonly bool _origRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        internal LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _err;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        internal string Output => _out.ToString() + _err.ToString();

        public void Dispose()
        {
            Logger.Out = _origOut;
            Logger.Error = _origErr;
            Logger.Quiet = _origQuiet;
            Logger.RealQuiet = _origRealQuiet;
            _out.Dispose();
            _err.Dispose();
        }
    }
}
