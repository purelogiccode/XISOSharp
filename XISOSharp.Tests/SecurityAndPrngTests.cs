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

        foreach (string file in _tempFiles)
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

    private static long RedumpLengthForTest(long maxStartSectors = 6000000) =>
        // redumpLength = (maxStart+4096)*SectorSize so that maxStart is high enough
        (maxStartSectors + 4096) * Constants.SectorSize;

    // -----------------------------------------------------------------
    // SecuritySectors.ParseLines
    // -----------------------------------------------------------------

    [Fact]
    public void ParseLines_ValidXgd1_16Ranges_Succeeds()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest(100000);
        IEnumerable<string> lines = Enumerable.Range(0, 16).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 0, quiet: true);
        Assert.NotNull(result);
        Assert.Equal(16, result.Length);
        for (int i = 0; i < 16; i++)
            Assert.Equal(i * 5000, result[i]);
    }

    [Fact]
    public void ParseLines_ValidXgd2_OneRange_Succeeds()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "1000-5095" };
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(1000, result[0]);
    }

    [Fact]
    public void ParseLines_ValidXgd2_TwoRanges_OnlyFirstKept()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "1000-5095", "2000-6095" };
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        // Per implementation, for xgdType !=0 only first range is kept, but lineCount validation expects 1 or 2
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(1000, result[0]);
    }

    [Fact]
    public void ParseLines_EmptyLines_AreIgnored()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "", "  ", "1000-5095", "", "  " };
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.NotNull(result);
        Assert.Single(result);
    }

    [Fact]
    public void ParseLines_InvalidFormat_MissingDash_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "1000:5095" };
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseLines_InvalidFormat_NonNumeric_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "abc-def" };
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseLines_InvalidLength_WrongGap_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "1000-5094" }; // gap 4094 not 4095
        int[]? result = SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);

        string[] lines2 = new[] { "1000-5096" }; // gap 4096
        Assert.Null(SecuritySectors.ParseLines(lines2, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_OutOfBounds_NegativeStart_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines = new[] { "-1-4094" };
        Assert.Null(SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_OutOfBounds_BeyondMaxStart_ReturnsNull()
    {
        Logger.Quiet = true;
        // small redump length so maxStart is small
        const long redumpLength = (5000 + 4096) * Constants.SectorSize; // maxStart = 5000
        string[] lines = new[] { "6000-10095" }; // start 6000 > maxStart 5000
        Assert.Null(SecuritySectors.ParseLines(lines, redumpLength, xgdType: 2, quiet: true));
    }

    [Fact]
    public void ParseLines_WrongCount_Xgd1_Not16_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        IEnumerable<string> lines15 = Enumerable.Range(0, 15).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        Assert.Null(SecuritySectors.ParseLines(lines15, redumpLength, xgdType: 0, quiet: true));

        IEnumerable<string> lines17 = Enumerable.Range(0, 17).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        Assert.Null(SecuritySectors.ParseLines(lines17, redumpLength, xgdType: 0, quiet: true));
    }

    [Fact]
    public void ParseLines_WrongCount_Xgd2_Not1Or2_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string[] lines0 = Array.Empty<string>();
        Assert.Null(SecuritySectors.ParseLines(lines0, redumpLength, xgdType: 2, quiet: true));

        string[] lines3 = new[] { "1000-5095", "2000-6095", "3000-7095" };
        Assert.Null(SecuritySectors.ParseLines(lines3, redumpLength, xgdType: 2, quiet: true));
    }

    // -----------------------------------------------------------------
    // SecuritySectors.ParseFile
    // -----------------------------------------------------------------

    [Fact]
    public void ParseFile_ValidFile_Succeeds()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest(100000);
        IEnumerable<string> lines = Enumerable.Range(0, 16).Select(i => $"{i * 5000}-{(i * 5000) + 4095}");
        string tmp = Path.Combine(Path.GetTempPath(), $"sectors_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(tmp, lines, Encoding.UTF8);
        _tempFiles.Add(tmp);

        int[]? result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 0, quiet: true);
        Assert.NotNull(result);
        Assert.Equal(16, result.Length);
    }

    [Fact]
    public void ParseFile_MissingFile_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.txt");
        int[]? result = SecuritySectors.ParseFile(missing, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFile_InvalidContent_ReturnsNull()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string tmp = Path.Combine(Path.GetTempPath(), $"sectors_bad_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, "not-a-range\n", Encoding.UTF8);
        _tempFiles.Add(tmp);
        int[]? result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 2, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFile_Xgd2_TwoRanges_Succeeds()
    {
        Logger.Quiet = true;
        long redumpLength = RedumpLengthForTest();
        string tmp = Path.Combine(Path.GetTempPath(), $"sectors2_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(tmp, Contents, Encoding.UTF8);
        _tempFiles.Add(tmp);
        int[]? result = SecuritySectors.ParseFile(tmp, redumpLength, xgdType: 1, quiet: true);
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
        XboxPrng prng = new(0);
        using MemoryStream ms = new();
        prng.WriteSectors(ms, 2);
        Assert.Equal(2 * Constants.SectorSize, ms.Length);
    }

    [Fact]
    public void XboxPrng_WriteSectors_SameSeed_SameOutput()
    {
        XboxPrng prng1 = new(12345);
        XboxPrng prng2 = new(12345);
        using MemoryStream ms1 = new();
        using MemoryStream ms2 = new();
        prng1.WriteSectors(ms1, 3);
        prng2.WriteSectors(ms2, 3);
        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_WriteSectors_DifferentSeeds_DifferentOutput()
    {
        XboxPrng prng1 = new(0);
        XboxPrng prng2 = new(1);
        using MemoryStream ms1 = new();
        using MemoryStream ms2 = new();
        prng1.WriteSectors(ms1, 2);
        prng2.WriteSectors(ms2, 2);
        Assert.NotEqual(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_SimulateSectors_AdvancesState()
    {
        XboxPrng prngA = new(42);
        XboxPrng prngB = new(42);

        // prngA: simulate 5 sectors then write 1
        prngA.SimulateSectors(5);
        using MemoryStream msA = new();
        prngA.WriteSectors(msA, 1);
        byte[] afterSimulate = msA.ToArray();

        // prngB: write 6 sectors, discard first 5
        using MemoryStream msB = new();
        prngB.WriteSectors(msB, 6);
        byte[] all = msB.ToArray();
        byte[] lastSector = all.Skip(5 * Constants.SectorSize).Take(Constants.SectorSize).ToArray();

        Assert.Equal(lastSector, afterSimulate);
    }

    [Fact]
    public void XboxPrng_SimulateSectors_Zero_DoesNotAdvance()
    {
        XboxPrng prng1 = new(7);
        XboxPrng prng2 = new(7);
        prng1.SimulateSectors(0);
        using MemoryStream ms1 = new();
        using MemoryStream ms2 = new();
        prng1.WriteSectors(ms1, 1);
        prng2.WriteSectors(ms2, 1);
        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    [Fact]
    public void XboxPrng_WriteSectors_ToFileStream_WritesCorrectly()
    {
        XboxPrng prng = new(99);
        string tmp = Path.Combine(Path.GetTempPath(), $"prng_{Guid.NewGuid():N}.bin");
        _tempFiles.Add(tmp);
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            prng.WriteSectors(fs, 1);
        }

        Assert.Equal(Constants.SectorSize, new FileInfo(tmp).Length);
        // Write via MemoryStream and compare
        XboxPrng prng2 = new(99);
        using MemoryStream ms = new();
        prng2.WriteSectors(ms, 1);
        Assert.Equal(ms.ToArray(), File.ReadAllBytes(tmp));
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeedZero()
    {
        const uint seed = 0;
        XboxPrng prng = new(seed);
        using MemoryStream ms = new();
        prng.WriteSectors(ms, 2);
        byte[] sectors = ms.ToArray();
        // TryGetSeed expects first 4096 bytes (2 sectors)
        bool ok = XboxPrng.TryGetSeed(sectors, out uint recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeed42()
    {
        const uint seed = 42;
        XboxPrng prng = new(seed);
        using MemoryStream ms = new();
        prng.WriteSectors(ms, 2);
        byte[] sectors = ms.ToArray();
        bool ok = XboxPrng.TryGetSeed(sectors, out uint recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_RecoversSeed_MaxByteBoundary()
    {
        // Test a seed that uses different FixedSeed index (seed & 7)
        const uint seed = 7; // last entry in FixedSeeds
        XboxPrng prng = new(seed);
        using MemoryStream ms = new();
        prng.WriteSectors(ms, 2);
        byte[] sectors = ms.ToArray();
        bool ok = XboxPrng.TryGetSeed(sectors, out uint recovered);
        Assert.True(ok);
        Assert.Equal(seed, recovered);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_InvalidData_ReturnsFalse()
    {
        byte[] random = new byte[Constants.SectorSize * 2];
        new Random(123).NextBytes(random);
        // It's astronomically unlikely that random data matches any seed's PRNG output for 4096 bytes.
        // Should return false.
        bool ok = XboxPrng.TryGetSeed(random, out _);
        Assert.False(ok);
    }

    [Fact]
    public void XboxPrng_TryGetSeed_PreCanceledToken_ReturnsFalseImmediately()
    {
        // BUG-LIB-041: the search previously took no token and could burn CPU
        // for hours without any way to stop it.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        byte[] random = new byte[Constants.SectorSize * 2];
        new Random(123).NextBytes(random);

        Assert.False(XboxPrng.TryGetSeed(random, out _, cts.Token));
    }

    [Fact]
    public void XboxPrng_ExtractSeed_InvalidPath_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.iso");
        Assert.Throws<FileNotFoundException>(() => XboxPrng.ExtractSeed(missing, 0, quiet: true));
    }

    [Fact]
    public void XboxPrng_ExtractSeed_InvalidIso_ReturnsNull()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"notxiso_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, new byte[Constants.SectorSize * 4]);
        _tempFiles.Add(tmp);
        using FileStream fs = new(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
        uint? result = XboxPrng.ExtractSeed(fs, 0, quiet: true);
        Assert.Null(result);
    }

    [Fact]
    public void XboxPrng_ExtractSeed_StringPath_InvalidIso_ReturnsNull()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"notxiso2_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, new byte[Constants.SectorSize * 4]);
        _tempFiles.Add(tmp);
        uint? result = XboxPrng.ExtractSeed(tmp, 0, quiet: true);
        Assert.Null(result);
    }
}
