namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoOperations"/>: filler extract, seed, wipe, trim.
/// </summary>
[Collection("Sequential")]
public class XisoOperationsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

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

        foreach (var f in _tempFiles)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch
            {
                // ignored
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_ops_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSourceDir(Action<string> populate)
    {
        var src = Path.Combine(Path.GetTempPath(), $"xiso_ops_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        _tempDirs.Add(src);
        populate(src);
        return src;
    }

    private string CreateIso(string srcDir, int? prependSectors = null)
    {
        var outDir = CreateTempDir();
        var result = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null,
            prependSectors: prependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
        return isoPath;
    }

    private static void PopulateSimple(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "b.txt"), new string('x', 5000));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "c.txt"), "nested content");
        var rnd = new Random(42);
        var data = new byte[7000];
        rnd.NextBytes(data);
        File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
    }

    // -----------------------------------------------------------------------
    // ExtractFiller
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractFiller_CreatesFillerFile_QuietTrue()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "out.filler");

        var ok = XisoOperations.ExtractFiller(iso, fillerPath, 0, null, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(fillerPath));
        // Filler is gaps between file extents; should be non-empty for a populated ISO
        Assert.True(new FileInfo(fillerPath).Length > 0);
    }

    [Fact]
    public void ExtractFiller_CreatesFillerFile_QuietFalse()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "out.filler");

        var ok = XisoOperations.ExtractFiller(iso, fillerPath, 0, null, quiet: false);

        Assert.True(ok);
        Assert.True(File.Exists(fillerPath));
        Assert.True(new FileInfo(fillerPath).Length > 0);
    }

    [Fact]
    public void ExtractFiller_FileStreamOverload_Succeeds()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "stream.filler");

        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var ok = XisoOperations.ExtractFiller(fs, 0, fs.Length, fillerPath, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(fillerPath));
        Assert.True(new FileInfo(fillerPath).Length > 0);
    }

    [Fact]
    public void ExtractFiller_WithXisoLengthOverride_Succeeds()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "override.filler");
        var isoLen = new FileInfo(iso).Length;

        var ok = XisoOperations.ExtractFiller(iso, fillerPath, 0, xisoLengthOverride: isoLen, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(fillerPath));
    }

    [Fact]
    public void ExtractFiller_WithIsoOffset_PrependedIso_Succeeds()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src, prependSectors: 16);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "prepend.filler");
        long offset = 16L * Constants.SectorSize;

        var ok = XisoOperations.ExtractFiller(iso, fillerPath, offset, null, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(fillerPath));
    }

    [Fact]
    public void ExtractFiller_FillerSizeMatchesExpected()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var fillerPath = Path.Combine(outDir, "size.filler");

        var ok = XisoOperations.ExtractFiller(iso, fillerPath, 0, null, quiet: true);
        Assert.True(ok);

        // Compute expected filler size via XisoRanges
        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        (List<(uint Start, uint End)> sys, List<(uint Start, uint End)> files) = XisoRanges.GetXisoRanges(fs, 0, true);
        var merged = XisoRanges.MergeRanges(sys, files);
        long validBytes = 0;
        foreach ((uint s, uint e) in merged) validBytes += (e - s + 1L) * Constants.SectorSize;
        long expectedFiller = fs.Length - validBytes;

        Assert.Equal(expectedFiller, new FileInfo(fillerPath).Length);
    }

    [Fact]
    public void ExtractFiller_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "nonexistent.iso");
        var filler = Path.Combine(outDir, "out.filler");

        Assert.Throws<FileNotFoundException>(() => XisoOperations.ExtractFiller(missing, filler, 0, null, true));
    }

    [Fact]
    public void ExtractFiller_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var filler = Path.Combine(outDir, "out.filler");

        Assert.Throws<EndOfStreamException>(() => XisoOperations.ExtractFiller(bad, filler, 0, null, true));
    }

    [Fact]
    public void ExtractFiller_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var filler = Path.Combine(outDir, "cancel.filler");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoOperations.ExtractFiller(iso, filler, 0, null, true, cts.Token));
    }

    // -----------------------------------------------------------------------
    // Seed extraction (XGD1 only)
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractSeed_NonXgd1_ReturnsNull()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);

        var seed = XisoOperations.ExtractSeed(iso, 0, quiet: true);

        Assert.Null(seed);
    }

    [Fact]
    public void TryExtractSeed_NonXgd1_ReturnsFalseAndDoesNotCreateFile()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var seedPath = Path.Combine(outDir, "seed.bin");
        if (File.Exists(seedPath)) File.Delete(seedPath);

        var ok = XisoOperations.TryExtractSeed(iso, seedPath, 0, quiet: true);

        Assert.False(ok);
        Assert.False(File.Exists(seedPath));
    }

    [Fact]
    public void TryExtractSeed_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "nonexistent.iso");
        var seedPath = Path.Combine(outDir, "seed.bin");

        Assert.Throws<FileNotFoundException>(() => XisoOperations.TryExtractSeed(missing, seedPath, 0, true));
    }

    [Fact]
    public void ExtractSeed_WithIsoOffset_PrependedIso_ReturnsNullForNonXgd1()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src, prependSectors: 16);
        long offset = 16L * Constants.SectorSize;

        var seed = XisoOperations.ExtractSeed(iso, offset, quiet: true);

        Assert.Null(seed);
        var outDir = CreateTempDir();
        var seedPath = Path.Combine(outDir, "seed2.bin");
        var ok = XisoOperations.TryExtractSeed(iso, seedPath, offset, quiet: true);
        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // WipeFiller
    // -----------------------------------------------------------------------

    [Fact]
    public void WipeFiller_CreatesOutputSameSizeAndExtractable()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var wiped = Path.Combine(outDir, "wiped.iso");

        var ok = XisoOperations.WipeFiller(iso, wiped, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(wiped));
        Assert.Equal(new FileInfo(iso).Length, new FileInfo(wiped).Length);

        // Wiped ISO should still be extractable and preserve file content
        var extractDir = CreateTempDir();
        var res = XisoReader.Extract(wiped, extractDir, false);
        Assert.Equal(0, res);
        Assert.True(File.Exists(Path.Combine(extractDir, "a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(extractDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "sub", "c.txt")));
    }

    [Fact]
    public void WipeFiller_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "nonexistent.iso");
        var wiped = Path.Combine(outDir, "wiped.iso");

        Assert.Throws<FileNotFoundException>(() => XisoOperations.WipeFiller(missing, wiped, 0, true));
    }

    [Fact]
    public void WipeFiller_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var wiped = Path.Combine(outDir, "wiped.iso");

        Assert.Throws<EndOfStreamException>(() => XisoOperations.WipeFiller(bad, wiped, 0, true));
    }

    [Fact]
    public void WipeFiller_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var wiped = Path.Combine(outDir, "wiped.iso");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoOperations.WipeFiller(iso, wiped, 0, true, cts.Token));
    }

    // -----------------------------------------------------------------------
    // TrimXiso
    // -----------------------------------------------------------------------

    [Fact]
    public void TrimXiso_WithOutput_ReducesOrEqualsSizeAndRemainsValid()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var trimmed = Path.Combine(outDir, "trimmed.iso");
        long origLen = new FileInfo(iso).Length;

        var ok = XisoOperations.TrimXiso(iso, trimmed, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(trimmed));
        long trimmedLen = new FileInfo(trimmed).Length;
        Assert.True(trimmedLen <= origLen, $"Trimmed length {trimmedLen} should be <= original {origLen}");
        Assert.Equal(0, trimmedLen % Constants.SectorSize);

        // Trimmed ISO should still be auditable / extractable
        var extractDir = CreateTempDir();
        var res = XisoReader.Extract(trimmed, extractDir, false);
        Assert.Equal(0, res);
        Assert.True(File.Exists(Path.Combine(extractDir, "a.txt")));
    }

    [Fact]
    public void TrimXiso_InPlace_TrimsFileAndRemainsValid()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var inPlace = Path.Combine(outDir, "inplace.iso");
        File.Copy(iso, inPlace, true);
        long before = new FileInfo(inPlace).Length;

        var ok = XisoOperations.TrimXiso(inPlace, null, 0, quiet: true);

        Assert.True(ok);
        long after = new FileInfo(inPlace).Length;
        Assert.True(after <= before);
        Assert.Equal(0, after % Constants.SectorSize);

        var extractDir = CreateTempDir();
        var res = XisoReader.Extract(inPlace, extractDir, false);
        Assert.Equal(0, res);
        Assert.True(File.Exists(Path.Combine(extractDir, "b.txt")));
    }

    [Fact]
    public void TrimXiso_InPlace_WithExplicitSamePath_TrimsFile()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var copy = Path.Combine(outDir, "copy.iso");
        File.Copy(iso, copy, true);
        long before = new FileInfo(copy).Length;

        // Passing the same path as output should trigger in-place logic
        var ok = XisoOperations.TrimXiso(copy, copy, 0, quiet: true);

        Assert.True(ok);
        Assert.True(new FileInfo(copy).Length <= before);
    }

    [Fact]
    public void TrimXiso_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "nonexistent.iso");
        var trimmed = Path.Combine(outDir, "trimmed.iso");

        Assert.Throws<FileNotFoundException>(() => XisoOperations.TrimXiso(missing, trimmed, 0, true));
    }

    [Fact]
    public void TrimXiso_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var trimmed = Path.Combine(outDir, "trimmed.iso");

        Assert.Throws<EndOfStreamException>(() => XisoOperations.TrimXiso(bad, trimmed, 0, true));
    }

    [Fact]
    public void TrimXiso_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var trimmed = Path.Combine(outDir, "trimmed.iso");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoOperations.TrimXiso(iso, trimmed, 0, true, cts.Token));
    }

    // -----------------------------------------------------------------------
    // WipeAndTrim
    // -----------------------------------------------------------------------

    [Fact]
    public void WipeAndTrim_CreatesSmallerOutputAndRemainsValid()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var wipeTrimmed = Path.Combine(outDir, "wipetrim.iso");
        long origLen = new FileInfo(iso).Length;

        var ok = XisoOperations.WipeAndTrim(iso, wipeTrimmed, 0, quiet: true);

        Assert.True(ok);
        Assert.True(File.Exists(wipeTrimmed));
        long newLen = new FileInfo(wipeTrimmed).Length;
        Assert.True(newLen <= origLen);
        Assert.Equal(0, newLen % Constants.SectorSize);

        // Output should be at most as large as TrimXiso output (wipe+trim is <= trim+wipe order)
        var trimmed = Path.Combine(outDir, "trimmed.iso");
        XisoOperations.TrimXiso(iso, trimmed, 0, true);
        Assert.True(newLen <= new FileInfo(trimmed).Length);

        // Verify extractable
        var extractDir = CreateTempDir();
        var res = XisoReader.Extract(wipeTrimmed, extractDir, false);
        Assert.Equal(0, res);
        Assert.True(File.Exists(Path.Combine(extractDir, "a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(extractDir, "a.txt")));
    }

    [Fact]
    public void WipeAndTrim_MissingFile_ThrowsFileNotFoundException()
    {
        var outDir = CreateTempDir();
        var missing = Path.Combine(outDir, "nonexistent.iso");
        var outPath = Path.Combine(outDir, "wipetrim.iso");

        Assert.Throws<FileNotFoundException>(() => XisoOperations.WipeAndTrim(missing, outPath, 0, true));
    }

    [Fact]
    public void WipeAndTrim_InvalidIso_ThrowsEndOfStreamException()
    {
        var outDir = CreateTempDir();
        var bad = Path.Combine(outDir, "bad.iso");
        File.WriteAllBytes(bad, new byte[100]);
        var outPath = Path.Combine(outDir, "wipetrim.iso");

        Assert.Throws<EndOfStreamException>(() => XisoOperations.WipeAndTrim(bad, outPath, 0, true));
    }

    [Fact]
    public void WipeAndTrim_Cancellation_ThrowsOperationCanceledException()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var outPath = Path.Combine(outDir, "wipetrim.iso");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XisoOperations.WipeAndTrim(iso, outPath, 0, true, cts.Token));
    }

    [Fact]
    public void ExtractFiller_AndWipeFiller_FillerAndWipedAreConsistent()
    {
        var src = CreateSourceDir(PopulateSimple);
        var iso = CreateIso(src);
        var outDir = CreateTempDir();
        var filler = Path.Combine(outDir, "filler.bin");
        var wiped = Path.Combine(outDir, "wiped.iso");

        Assert.True(XisoOperations.ExtractFiller(iso, filler, 0, null, true));
        Assert.True(XisoOperations.WipeFiller(iso, wiped, 0, true));

        long fillerLen = new FileInfo(filler).Length;
        long isoLen = new FileInfo(iso).Length;
        long wipedLen = new FileInfo(wiped).Length;

        // Wiped should be same size as original, filler + valid data == iso size
        Assert.Equal(isoLen, wipedLen);
        using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        (List<(uint Start, uint End)> sys, List<(uint Start, uint End)> files) = XisoRanges.GetXisoRanges(fs, 0, true);
        var merged = XisoRanges.MergeRanges(sys, files);
        long validBytes = 0;
        foreach ((uint s, uint e) in merged) validBytes += (e - s + 1L) * Constants.SectorSize;
        Assert.Equal(validBytes + fillerLen, isoLen);
    }
}