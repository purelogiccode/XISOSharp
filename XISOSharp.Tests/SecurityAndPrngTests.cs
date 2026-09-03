using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="SecuritySectors"/> and <see cref="XboxPrng"/>.
/// </summary>
[Collection("Sequential")]
public class SecurityAndPrngTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];
    private static readonly string[] Contents = new[] { "1000-5095", "9000-13095" };

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

        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static long RedumpLengthForTest(long maxStartSectors = 6000000)
    {
        // redumpLength = (maxStart+4096)*SectorSize so that maxStart is high enough
        return (maxStartSectors + 4096) * Constants.SectorSize;
    }

    // -----------------------------------------------------------------
    // SecuritySectors.ParseLines
    // -----------------------------------------------------------------

    [Fact]
    public void ParseLines_ValidXgd1_16Ranges_Succeeds()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest(100000);
        var lines = Enumerable.Range(0, 16).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 0, quiet: true);
        Assert.NotNull(result);
        Assert.Equal(16, result.Length);
        for (var i = 0; i < 16; i++)
            Assert.Equal(i * 5000, result[i]);
    }

    [Fact]
    public void ParseLines_ValidXgd2_OneRange_Succeeds()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "1000-5095" };
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(1000, result[0]);
    }

    [Fact]
    public void ParseLines_ValidXgd2_TwoRanges_OnlyFirstKept()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "1000-5095", "2000-6095" };
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        // Per implementation, for xgdType !=0 only first range is kept, but lineCount validation expects 1 or 2
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(1000, result[0]);
    }

    [Fact]
    public void ParseLines_EmptyLines_AreIgnored()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "", "  ", "1000-5095", "", "  " };
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public void ParseLines_InvalidFormat_MissingDash_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "1000:5095" };
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseLines_InvalidFormat_NonNumeric_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "abc-def" };
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseLines_InvalidLength_WrongGap_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "1000-5094" }; // gap 4094 not 4095
        var result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);

        var lines2 = new[] { "1000-5096" }; // gap 4096
        Assert.Null(SecuritySectors.ParseLines(lines2, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_OutOfBounds_NegativeStart_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines = new[] { "-1-4094" };
        Assert.Null(SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_OutOfBounds_BeyondMaxStart_ReturnsNull()
    {
        Logger.Quiet = true;
        // small redump length so maxStart is small
        const long redumpLength = (5000 + 4096) * Constants.SectorSize; // maxStart = 5000
        var lines = new[] { "6000-10095" }; // start 6000 > maxStart 5000
        Assert.Null(SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_WrongCount_Xgd1_Not16_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines15 = Enumerable.Range(0, 15).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        Assert.Null(SecuritySectors.ParseLines(lines15, redumpLength, xgdType: 0, quiet: true));

        var lines17 = Enumerable.Range(0, 17).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        Assert.Null(SecuritySectors.ParseLines(lines17, redumpLength, xgdType: 0, quiet: true));
    }

    [Fact]
    public void ParseLines_WrongCount_Xgd2_Not1Or2_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var lines0 = Array.Empty<string>();
        Assert.Null(SecuritySectors.ParseLines(lines0, redumpLength, xgdType: 2, quiet: true));

        var lines3 = new[] { "1000-5095", "2000-6095", "3000-7095" };
        Assert.Null(SecuritySectors.ParseLines(lines3, redumpLength, xgdType: 2, quiet: true));
    }

    // -----------------------------------------------------------------
    // SecuritySectors.ParseFile
    // -----------------------------------------------------------------

    [Fact]
    public void ParseFile_ValidFile_Succeeds()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest(100000);
        var lines = Enumerable.Range(0, 16).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        var tmp = Path.Combine(Path.GetTempPath(), $"sectors_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(tmp, lines, Encoding.UTF8);
        _tempFiles.Add(tmp);

        var result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 0, quiet: true);
        Assert.NotNull(result);
        Assert.Equal(16, result.Length);
    }

    [Fact]
    public void ParseFile_MissingFile_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.txt");
        var result = SecuritySectors.ParseFile(missing, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFile_InvalidContent_ReturnsNull()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var tmp = Path.Combine(Path.GetTempPath(), $"sectors_bad_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, "not-a-range\n", Encoding.UTF8);
        _tempFiles.Add(tmp);
        var result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFile_Xgd2_TwoRanges_Succeeds()
    {
        Logger.Quiet = true;
        var redumpLength = RedumpLengthForTest();
        var tmp = Path.Combine(Path.GetTempPath(), $"sectors2_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(tmp, Contents, Encoding.UTF8);
        _tempFiles.Add(tmp);
        var result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 1, quiet: true);
        Assert.NotNull(result);
        Assert.Single(result); // only first kept per logic
        Assert.Equal(1000, result[0]);
    }

    // -----------------------------------------------------------------
    // XboxPrng
    // -----------------------------------------------------------------

    [Fact]
    public void XboxPrng_WriteSectors_WritesCorrectByteCount()
    {
        var prng = new XboxPrng(0);
        using var ms = new MemoryStream();
        prng.WriteSectors(ms, 2);
        Assert.Equal(2 * Constants.SectorSize, ms.Length);
    }

    [Fact]
    public void XboxPrng_WriteSectors_SameSeed_SameOutput()
    {
        var prng1 = new XboxPrng(12345);
        var prng2 = new XboxPrng(12345);
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        prng1.WriteSectors(ms1, 3);
        prng2.WriteSectors(ms2, 3);
        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_WriteSectors_DifferentSeeds_DifferentOutput()
    {
        var prng1 = new XboxPrng(0);
        var prng2 = new XboxPrng(1);
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        prng1.WriteSectors(ms1, 2);
        prng2.WriteSectors(ms2, 2);
        Assert.NotEqual(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_SimulateSectors_AdvancesState()
    {
        var prngA = new XboxPrng(42);
        var prngB = new XboxPrng(42);

        // prngA: simulate 5 sectors then write 1
        prngA.SimulateSectors(5);
        using var msA = new MemoryStream();
        prngA.WriteSectors(msA, 1);
        var afterSimulate = msA.ToArray();

        // prngB: write 6 sectors, discard first 5
        using var msB = new MemoryStream();
        prngB.WriteSectors(msB, 6);
        var all = msB.ToArray();
        var lastSector = all.Skip(5 * Constants.SectorSize).Take(Constants.SectorSize).ToArray();

        Assert.Equal(lastSector, afterSimulate);
    }

    [Fact]
    public void XboxPrng_SimulateSectors_Zero_DoesNotAdvance()
    {
        var prng1 = new XboxPrng(7);
        var prng2 = new XboxPrng(7);
        prng1.SimulateSectors(0);
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        prng1.WriteSectors(ms1, 1);
        prng2.WriteSectors(ms2, 1);
        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_WriteSectors_ToFileStream_WritesCorrectly()
    {
        var prng = new XboxPrng(99);
        var tmp = Path.Combine(Path.GetTempPath(), $"prng_{Guid.NewGuid():N}.bin");
        _tempFiles.Add(tmp);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            prng.WriteSectors(fs, 1);
        }

        Assert.Equal(Constants.SectorSize, new FileInfo(tmp).Length);
        // Write via MemoryStream and compare
        var prng2 = new XboxPrng(99);
        using var ms = new MemoryStream();
        prng2.WriteSectors(ms, 1);
        Assert.Equal(ms.ToArray(), File.ReadAllBytes(tmp));
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeedZero()
    {
        const uint seed = 0;
        var prng = new XboxPrng(seed);
        using var ms = new MemoryStream();
        prng.WriteSectors(ms, 2);
        var sectors = ms.ToArray();
        // TryGetSeed expects first 4096 bytes (2 sectors)
        var ok = XboxPrng.TryGetSeed(sectors, out var recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeed42()
    {
        const uint seed = 42;
        var prng = new XboxPrng(seed);
        using var ms = new MemoryStream();
        prng.WriteSectors(ms, 2);
        var sectors = ms.ToArray();
        var ok = XboxPrng.TryGetSeed(sectors, out var recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeed_MaxByteBoundary()
    {
        // Test a seed that uses different FixedSeed index (seed & 7)
        const uint seed = 7; // last entry in FixedSeeds
        var prng = new XboxPrng(seed);
        using var ms = new MemoryStream();
        prng.WriteSectors(ms, 2);
        var sectors = ms.ToArray();
        var ok = XboxPrng.TryGetSeed(sectors, out var recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_InvalidData_ReturnsFalse()
    {
        var random = new byte[Constants.SectorSize * 2];
        new Random(123).NextBytes(random);
        // It's astronomically unlikely that random data matches any seed's PRNG output for 4096 bytes.
        // Should return false.
        var ok = XboxPrng.TryGetSeed(random, out _);
        Assert.False(ok);
    }

    [Fact]
    public void XboxPrng_ExtractSeed_InvalidPath_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.iso");
        Assert.Throws<FileNotFoundException>(() => XboxPrng.ExtractSeed(missing, 0, quiet: true));
    }

    [Fact]
    public void XboxPrng_ExtractSeed_InvalidIso_ReturnsNull()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"notxiso_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, new byte[Constants.SectorSize * 4]);
        _tempFiles.Add(tmp);
        using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = XboxPrng.ExtractSeed(fs, 0, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void XboxPrng_ExtractSeed_StringPath_InvalidIso_ReturnsNull()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"notxiso2_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, new byte[Constants.SectorSize * 4]);
        _tempFiles.Add(tmp);
        var result = XboxPrng.ExtractSeed(tmp, 0, quiet: true);
        Assert.Null(result);
    }
}