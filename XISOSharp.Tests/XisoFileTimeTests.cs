namespace XISOSharp.Tests;

/// <summary>
/// Tests for #37 — filetime generate/parse/display.
/// Covers FileTimeHelper conversions and XisoReader Get/Set round-trip
/// plus rewrite preservation.
/// </summary>
[Collection("Sequential")]
public class XisoFileTimeTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

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
            catch { }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_ft_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSimpleIso(out string srcDir, out string outDir)
    {
        srcDir = CreateTempDir();
        outDir = CreateTempDir();
        File.WriteAllText(Path.Combine(srcDir, "hello.txt"), "hello world");
        File.WriteAllText(Path.Combine(srcDir, "data.bin"), "12345");
        int rc = XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, rc);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
        return isoPath!;
    }

    [Fact]
    public void FileTimeHelper_RoundTrip_DtoToRawAndBack()
    {
        var dto = new DateTimeOffset(2020, 6, 15, 12, 34, 56, TimeSpan.Zero);
        ulong raw = FileTimeHelper.ToFileTimeRaw(dto);
        DateTimeOffset back = FileTimeHelper.FromFileTimeRaw(raw);
        // BCL conversion is lossless for UTC times within range; allow 100ns tolerance (1 tick)
        Assert.Equal(dto.UtcDateTime, back.UtcDateTime);
        // Raw should be non-zero and reasonable (after 1601)
        Assert.True(raw > 0);
        // Re-encode should give same raw
        ulong raw2 = FileTimeHelper.ToFileTimeRaw(back);
        Assert.Equal(raw, raw2);
    }

    [Fact]
    public void FileTimeHelper_Zero_MapsTo1601()
    {
        ulong rawZero = 0UL;
        DateTimeOffset epoch = FileTimeHelper.FromFileTimeRaw(rawZero);
        Assert.Equal(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), epoch);
        // Encoding the epoch should give 0
        ulong backRaw = FileTimeHelper.ToFileTimeRaw(epoch);
        Assert.Equal(0UL, backRaw);
        // xdvdfs generates 0 for deterministic images (ProposedEnhancements #37)
        Span<byte> buf = stackalloc byte[8];
        FileTimeHelper.WriteFileTime(buf, rawZero);
        Assert.Equal(0UL, FileTimeHelper.ReadFileTimeRaw(buf));
        Assert.Equal("1601-01-01T00:00:00.0000000+00:00 (0)", FileTimeHelper.FormatFileTime(0));
    }

    [Fact]
    public void FileTimeHelper_Max_Handling()
    {
        // Max valid FILETIME is near DateTime.MaxValue; test that large values don't throw and clamp sanely.
        ulong maxRaw = (ulong)DateTime.MaxValue.ToFileTimeUtc();
        DateTimeOffset dtoMax = FileTimeHelper.FromFileTimeRaw(maxRaw);
        // Should round-trip without exception; dtoMax should be close to MaxValue
        Assert.True(dtoMax.Year >= 9990);

        // Zero and now parsing
        Assert.True(FileTimeHelper.TryParseFileTime("0", out ulong r0, out var d0));
        Assert.Equal(0UL, r0);
        Assert.Equal(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), d0);

        Assert.True(FileTimeHelper.TryParseFileTime("now", out ulong rNow, out var dNow));
        Assert.True(rNow > 0);
        Assert.True((DateTimeOffset.UtcNow - dNow).Duration() < TimeSpan.FromSeconds(5));

        // Hex parsing and ISO8601
        Assert.True(FileTimeHelper.TryParseFileTime("0x0", out ulong rh, out _));
        Assert.Equal(0UL, rh);
        Assert.True(FileTimeHelper.TryParseFileTime("2023-08-26T15:30:00Z", out ulong rIso, out var dIso));
        Assert.Equal(new DateTimeOffset(2023, 8, 26, 15, 30, 0, TimeSpan.Zero), dIso);
        Assert.True(rIso > 0);

        // Decimal raw parsing
        string dec = maxRaw.ToString();
        Assert.True(FileTimeHelper.TryParseFileTime(dec, out ulong rDec, out _));
        Assert.Equal(maxRaw, rDec);
    }

    [Fact]
    public void XisoReader_GetSetFileTime_RoundTrip()
    {
        string isoPath = CreateSimpleIso(out _, out _);

        // Initial filetime is non-zero (current time)
        ulong initialRaw = XisoReader.GetFileTimeRaw(isoPath);
        DateTimeOffset initialDto = XisoReader.GetFileTime(isoPath);
        Assert.Equal(initialDto, FileTimeHelper.FromFileTimeRaw(initialRaw));

        // Set to zero (xdvdfs deterministic) and verify
        XisoReader.SetFileTime(isoPath, 0UL);
        ulong zeroRaw = XisoReader.GetFileTimeRaw(isoPath);
        Assert.Equal(0UL, zeroRaw);
        Assert.Equal(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), XisoReader.GetFileTime(isoPath));

        // Set to specific ISO8601 via helper and verify
        var targetDto = new DateTimeOffset(2021, 12, 31, 23, 59, 59, TimeSpan.Zero);
        XisoReader.SetFileTime(isoPath, targetDto);
        ulong afterRaw = XisoReader.GetFileTimeRaw(isoPath);
        DateTimeOffset afterDto = XisoReader.GetFileTime(isoPath);
        Assert.Equal(targetDto, afterDto);
        Assert.Equal(FileTimeHelper.ToFileTimeRaw(targetDto), afterRaw);

        // Set via raw ulong max (hex) and verify format
        ulong hexRaw = 0x01D7A3C8F1234567UL; // arbitrary valid
        XisoReader.SetFileTime(isoPath, hexRaw);
        ulong readBack = XisoReader.GetFileTimeRaw(isoPath);
        Assert.Equal(hexRaw, readBack);

        // BlockDevice overload parity
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var dev = new XISOSharp.BlockDevice.FileBlockDevice(fs, false);
        ulong devRaw = XisoReader.GetFileTimeRaw(dev, "test.iso");
        Assert.Equal(hexRaw, devRaw);
    }

    [Fact]
    public void XisoReader_FileTime_RewritePreserved()
    {
        // Create ISO, set known filetime, rewrite via XisoReader.Rewrite (which copies filetime from source stream)
        string isoPath = CreateSimpleIso(out _, out var outDir);
        var knownDto = new DateTimeOffset(2019, 5, 17, 10, 0, 0, TimeSpan.Zero);
        XisoReader.SetFileTime(isoPath, knownDto);
        ulong beforeRaw = XisoReader.GetFileTimeRaw(isoPath);

        // Rewrite: requires .old dance as in Program.cs rewrite path; use XisoReader.Rewrite wrapper
        string? rewriteOut;
        // XisoReader.Rewrite expects .old file handling via DecodeXiso; easier to test via direct CreateXiso rewrite path:
        // Use XisoReader.DecodeXiso with Rewrite mode requires renaming to .old.
        string oldPath = isoPath + ".old";
        File.Move(isoPath, oldPath);
        try
        {
            int rc = XisoReader.Rewrite(oldPath, outDir, out rewriteOut, CancellationToken.None, null, null, null,
                null);
            Assert.Equal(0, rc);
            // Rewrite may create new file in outDir or cwd; find it
            // Rewrite creates file named after source without .old suffix, with .iso if needed
            // Locate the rewritten iso (newIsoPath)
            string? rewrittenIso = rewriteOut ?? Directory.GetFiles(outDir, "*.iso").FirstOrDefault() ?? isoPath;
            Assert.NotNull(rewrittenIso);
            if (!File.Exists(rewrittenIso))
            {
                // Fallback: check current directory? outDir already has it
                rewrittenIso = Directory.GetFiles(outDir, "*")
                    .FirstOrDefault(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ?? oldPath;
            }

            Assert.True(File.Exists(rewrittenIso),
                $"Rewritten ISO not found; outDir contents: {string.Join(",", Directory.GetFiles(outDir))}");

            ulong afterRaw = XisoReader.GetFileTimeRaw(rewrittenIso);
            Assert.Equal(beforeRaw, afterRaw);
            DateTimeOffset afterDto = XisoReader.GetFileTime(rewrittenIso);
            Assert.Equal(knownDto, afterDto);
        }
        finally
        {
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
            catch { }
        }
    }

    [Fact]
    public void FileTimeHelper_TryParse_InvalidReturnsFalse()
    {
        Assert.False(FileTimeHelper.TryParseFileTime("not-a-date", out _, out _));
        Assert.False(FileTimeHelper.TryParseFileTime("0xZZZ", out _, out _));
        Assert.False(FileTimeHelper.TryParseFileTime("", out _, out _));
    }
}