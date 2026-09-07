using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoSalvager"/> Phase 2 (TODO #26): salvage rebuilds
/// from class-T/S corruption — bounded walk, staging, <c>CreateXiso</c>
/// repack, and re-audit — plus the <c>--salvage</c> / <c>--repair-out</c> CLI.
/// </summary>
[Collection("Sequential")]
public class XisoSalvageTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly TextWriter _savedOut = Logger.Out;
    private readonly StringWriter _logCapture = new();

    public XisoSalvageTests()
    {
        Logger.Out = _logCapture;
    }

    public void Dispose()
    {
        Logger.Out = _savedOut;
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
                /* best effort cleanup */
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

    private string CreateIso(Action<string> populate, string isoName)
    {
        var src = CreateTempDir("xiso_salv_src");
        populate(src);
        var dir = CreateTempDir("xiso_salv_iso");
        var result = XisoWriter.CreateXiso(src, dir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    private string CopyIso(string isoPath, string prefix)
    {
        var dest = Path.Combine(CreateTempDir(prefix), Path.GetFileName(isoPath));
        File.Copy(isoPath, dest);
        return dest;
    }

    private static long FindEntryHeader(byte[] img, long tableAbs, uint tableSize, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var tableEnd = tableAbs + tableSize;
        for (var i = tableAbs; i + 14 + nameBytes.Length <= Math.Min(tableEnd, img.Length); i++)
        {
            var match = true;
            for (var k = 0; k < nameBytes.Length; k++)
            {
                if (img[i + 14 + k] != nameBytes[k])
                {
                    match = false;
                    break;
                }
            }

            if (match && img[i + 13] == nameBytes.Length)
                return i;
        }

        Assert.Fail($"entry '{name}' not found in directory table at {tableAbs}");
        return -1;
    }

    private static (uint RootSector, uint RootSize, long RootAbs) RootLayout(string isoPath)
    {
        var vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid, $"fixture ISO invalid: {isoPath}");
        return (vol.RootDirSector, vol.RootDirSize, ((long)vol.RootDirSector * Constants.SectorSize) + vol.DiscLseek);
    }

    private static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static List<string> Sorted(IReadOnlyList<string> paths)
    {
        return paths.OrderBy(static p => p, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void DefaultOutputPath_ReplacesExtensionWithSalvagedIso()
    {
        var dir = CreateTempDir("xiso_salv_def");
        Assert.Equal(Path.Combine(dir, "game.salvaged.iso"),
            XisoSalvager.DefaultOutputPath(Path.Combine(dir, "game.iso")));
        Assert.Equal(Path.Combine(dir, "game.salvaged.iso"),
            XisoSalvager.DefaultOutputPath(Path.Combine(dir, "game.cso")));
        Assert.Throws<ArgumentException>(() => XisoSalvager.DefaultOutputPath(""));
    }

    [Fact]
    public void Salvage_CleanImage_CarriesAllAndPasses()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello world"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "data.bin"), [1, 2, 3, 4, 5]);
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "nested.txt"), "nested"u8.ToArray());
        }, "game.iso");
        var before = Sha256(isoPath);

        var result = XisoReader.Salvage(isoPath);

        Assert.True(result.Success);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.OutputIssues);
        Assert.Equal(XisoSalvager.DefaultOutputPath(isoPath), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(
            ["/data.bin", "/readme.txt", "/sub/", "/sub/nested.txt"],
            Sorted(result.Copied));
        Assert.True(XisoReader.AuditXiso(result.OutputPath).IsValid);

        // Round-trip: the rebuilt image holds identical bytes.
        var dest = CreateTempDir("xiso_salv_rt");
        XisoReader.CopyOut(result.OutputPath, "/sub/nested.txt", Path.Combine(dest, "nested.txt"));
        Assert.Equal("nested"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "nested.txt")));

        // The source is byte-identical: salvage only ever reads it.
        Assert.Equal(before, Sha256(isoPath));
    }

    [Fact]
    public void Salvage_ReservedBits_CarriedDespiteCorruptAttrs()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello"u8.ToArray()), "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var header = FindEntryHeader(img, RootLayout(bad).RootAbs, RootLayout(bad).RootSize, "readme.txt");
        img[header + 12] |= Constants.AttributeReservedMask;
        File.WriteAllBytes(bad, img);
        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Reserved attribute bits", StringComparison.Ordinal));

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/readme.txt"], result.Copied);
    }

    [Fact]
    public void Salvage_MissingTag_CarriedOutputPasses()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello"u8.ToArray()), "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        using (var fs = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength]);
        }

        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Optimized tag not found", StringComparison.Ordinal));

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/readme.txt"], result.Copied);
    }

    [Fact]
    public void Salvage_SeparatorName_SanitizedAndCarried()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "notex.txt"), "hello"u8.ToArray()), "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var (rootSector, rootSize, rootAbs) = RootLayout(bad);
        _ = rootSector;
        var header = FindEntryHeader(img, rootAbs, rootSize, "notex.txt");
        img[header + 14 + 3] = (byte)'/';
        File.WriteAllBytes(bad, img);

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/not_x.txt"], result.Copied);
        Assert.True(XisoReader.AuditXiso(result.OutputPath).IsValid);
    }

    [Fact]
    public void Salvage_SeparatorCollision_DropsLoserWithReport()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "x_y.txt"), "winner"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "xqy.txt"), "loser"u8.ToArray());
        }, "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var (rootSector, rootSize, rootAbs) = RootLayout(bad);
        _ = rootSector;
        var header = FindEntryHeader(img, rootAbs, rootSize, "xqy.txt");
        img[header + 14 + 1] = (byte)'/';
        File.WriteAllBytes(bad, img);

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/x_y.txt"], Sorted(result.Copied));
        Assert.Single(result.Skipped);
        Assert.Contains("separator-collision", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_ForgedFileSize_DropsEntryCarriesRest()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "good.txt"), "good"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "huge.txt"), "small"u8.ToArray());
        }, "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var (rootSector, rootSize, rootAbs) = RootLayout(bad);
        _ = rootSector;
        var header = FindEntryHeader(img, rootAbs, rootSize, "huge.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        File.WriteAllBytes(bad, img);

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/good.txt"], result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("huge.txt", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_ForgedStartSector_DropsEntryCarriesRest()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "good.txt"), "good"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "lost.txt"), "lost"u8.ToArray());
        }, "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var (rootSector, rootSize, rootAbs) = RootLayout(bad);
        _ = rootSector;
        var header = FindEntryHeader(img, rootAbs, rootSize, "lost.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0xFFFFFF00u);
        File.WriteAllBytes(bad, img);

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/good.txt"], result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("lost.txt", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_TruncatedImage_DropsCutFileOutputPasses()
    {
        var payload = new byte[100 * 1024];
        new Random(42).NextBytes(payload);
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "big.bin"), payload), "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        using (var fs = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(fs.Length - 50000);
        }

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Empty(result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("big.bin", result.Skipped[0], StringComparison.Ordinal);
        Assert.True(XisoReader.AuditXiso(result.OutputPath).IsValid);
    }

    [Fact]
    public void Salvage_RightSelfLoop_TerminatesCarriesReached()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var img = File.ReadAllBytes(bad);
        var (rootSector, rootSize, rootAbs) = RootLayout(bad);
        _ = rootSector;
        var header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        var self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        File.WriteAllBytes(bad, img);
        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Cycle detected", StringComparison.Ordinal));

        var result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/a.txt", "/b.txt"], Sorted(result.Copied));
        Assert.Contains(result.Skipped, static s => s.Contains("Cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public void Salvage_DeepTree_TerminatesAtDepthGate()
    {
        var src = CreateTempDir("xiso_salv_deep");
        var dir = src;
        for (var i = 0; i < 70; i++)
        {
            dir = Path.Combine(dir, "a");
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(Path.Combine(dir, "leaf.txt"), "leaf\n");
        var outDir = CreateTempDir("xiso_salv_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, "deep", null));
        Assert.NotNull(isoPath);

        var result = XisoReader.Salvage(isoPath);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Copied, static p => p.EndsWith("leaf.txt", StringComparison.Ordinal));
        Assert.Contains(result.Skipped,
            static s => s.Contains("maximum directory depth", StringComparison.Ordinal));
    }

    [Fact]
    public void Salvage_BadMagic_ThrowsFormat()
    {
        var path = Path.Combine(CreateTempDir("xiso_salv_bad"), "garbage.iso");
        File.WriteAllBytes(path, "not an xbox image at all"u8.ToArray());
        Assert.Throws<XisoFormatException>(() => XisoReader.Salvage(path));
    }

    [Fact]
    public void Salvage_MissingFile_ThrowsNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            XisoReader.Salvage(Path.Combine(CreateTempDir("xiso_salv_bad"), "no_such.iso")));
    }

    [Fact]
    public void Salvage_EmptyPath_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => XisoReader.Salvage(""));
    }

    [Fact]
    public void Salvage_RootBeyondLength_ThrowsFormat()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        var bad = CopyIso(isoPath, "xiso_salv_bad");
        var rootAbs = RootLayout(bad).RootAbs;
        using (var fs = new FileStream(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(rootAbs - 100);
        }

        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.Salvage(bad));
        Assert.Contains("no tree root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_CsoInput_ProducesPlainIso()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello cso"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "n.txt"), "n"u8.ToArray());
        }, "game.iso");
        var csoDir = CreateTempDir("xiso_salv_cso");
        var csoPath = Path.Combine(csoDir, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath, level: 6));

        var result = XisoReader.Salvage(csoPath);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(csoDir, "game.salvaged.iso"), result.OutputPath);
        Assert.Equal(["/readme.txt", "/sub/", "/sub/n.txt"], Sorted(result.Copied));
        var vol = XisoReader.GetVolumeInfo(result.OutputPath);
        Assert.True(vol.IsValid);
    }

    [Fact]
    public void Salvage_ExplicitOutput_Respected()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        var custom = Path.Combine(CreateTempDir("xiso_salv_custom"), "nested", "out.iso");

        var result = XisoReader.Salvage(isoPath, custom);

        Assert.Equal(custom, result.OutputPath);
        Assert.True(File.Exists(custom));
        Assert.True(result.Success);
    }

    [Fact]
    public void Salvage_OutputResalvage_CarriesAll()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        var first = XisoReader.Salvage(isoPath);
        Assert.True(first.Success);

        var second = XisoReader.Salvage(first.OutputPath, first.OutputPath + ".round2.iso");

        Assert.True(second.Success);
        Assert.Empty(second.Skipped);
        Assert.Equal(Sorted(first.Copied), Sorted(second.Copied));
    }

    [Fact]
    public void SalvageCli_EndToEnd_WritesDefaultOutput()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(0, Program.Main(["--salvage", isoPath]));

        var expected = XisoSalvager.DefaultOutputPath(isoPath);
        Assert.True(File.Exists(expected));
        Assert.True(XisoReader.AuditXiso(expected).IsValid);
        Assert.Contains("PASS", _logCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SalvageCli_RepairOut_OverridesOutputPath()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        var custom = Path.Combine(CreateTempDir("xiso_salv_cli"), "custom.iso");

        Assert.Equal(0, Program.Main(["--salvage", "--repair-out", custom, isoPath]));
        Assert.True(File.Exists(custom));
    }

    [Fact]
    public void SalvageCli_TruncatedImage_ReportsDroppedAndPasses()
    {
        var payload = new byte[100 * 1024];
        new Random(7).NextBytes(payload);
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "big.bin"), payload), "game.iso");
        using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(fs.Length - 50000);
        }

        Assert.Equal(0, Program.Main(["--salvage", isoPath]));

        var log = _logCapture.ToString();
        Assert.Contains("Dropped", log, StringComparison.Ordinal);
        Assert.Contains("big.bin", log, StringComparison.Ordinal);
        Assert.Contains("PASS", log, StringComparison.Ordinal);
    }

    [Fact]
    public void SalvageCli_ExtractCombined_UsageError()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(1, Program.Main(["-x", "--salvage", isoPath]));
    }

    [Fact]
    public void SalvageCli_RepairOutWithoutSalvage_Rejected()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(1, Program.Main(["--repair-out", "out.iso", isoPath]));
    }

    [Fact]
    public void SalvageCli_MissingOperand_UsageError()
    {
        Assert.Equal(1, Program.Main(["--salvage"]));
    }

    [Fact]
    public void SalvageCli_MissingFile_Fails()
    {
        Assert.Equal(1, Program.Main(["--salvage", Path.Combine(CreateTempDir("xiso_salv_cli"), "no.iso")]));
    }

    [Fact]
    public void SalvageCli_ExistingOutput_AssumeNoRefusesAssumeYesOverwrites()
    {
        var isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        var expected = XisoSalvager.DefaultOutputPath(isoPath);
        File.WriteAllText(expected, "sentinel");

        Assert.Equal(1, Program.Main(["--salvage", "-n", isoPath]));
        Assert.Equal("sentinel", File.ReadAllText(expected));

        Assert.Equal(0, Program.Main(["--salvage", "-y", isoPath]));
        Assert.NotEqual("sentinel", File.ReadAllText(expected), StringComparer.OrdinalIgnoreCase);
        Assert.True(XisoReader.AuditXiso(expected).IsValid);
    }
}
