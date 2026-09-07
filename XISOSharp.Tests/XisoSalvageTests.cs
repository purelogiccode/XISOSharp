using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.Cli;
using XISOSharp.Models;

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

        foreach (string dir in _tempDirs)
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
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(Action<string> populate, string isoName)
    {
        string src = CreateTempDir("xiso_salv_src");
        populate(src);
        string dir = CreateTempDir("xiso_salv_iso");
        int result = XisoWriter.CreateXiso(src, dir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.NotNull(created);
        return created;
    }

    private string CopyIso(string isoPath, string prefix)
    {
        string dest = Path.Combine(CreateTempDir(prefix), Path.GetFileName(isoPath));
        File.Copy(isoPath, dest);
        return dest;
    }

    private static long FindEntryHeader(byte[] img, long tableAbs, uint tableSize, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        long tableEnd = tableAbs + tableSize;
        for (long i = tableAbs; i + 14 + nameBytes.Length <= Math.Min(tableEnd, img.Length); i++)
        {
            bool match = true;
            for (int k = 0; k < nameBytes.Length; k++)
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
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid, $"fixture ISO invalid: {isoPath}");
        return (vol.RootDirSector, vol.RootDirSize, ((long)vol.RootDirSector * Constants.SectorSize) + vol.DiscLseek);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static List<string> Sorted(IReadOnlyList<string> paths) => paths.OrderBy(static p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public void DefaultOutputPath_ReplacesExtensionWithSalvagedIso()
    {
        string dir = CreateTempDir("xiso_salv_def");
        Assert.Equal(Path.Combine(dir, "game.salvaged.iso"),
            XisoSalvager.DefaultOutputPath(Path.Combine(dir, "game.iso")));
        Assert.Equal(Path.Combine(dir, "game.salvaged.iso"),
            XisoSalvager.DefaultOutputPath(Path.Combine(dir, "game.cso")));
        Assert.Throws<ArgumentException>(() => XisoSalvager.DefaultOutputPath(""));
    }

    [Fact]
    public void Salvage_CleanImage_CarriesAllAndPasses()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello world"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "data.bin"), [1, 2, 3, 4, 5]);
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "nested.txt"), "nested"u8.ToArray());
        }, "game.iso");
        string before = Sha256(isoPath);

        SalvageResult result = XisoReader.Salvage(isoPath);

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
        string dest = CreateTempDir("xiso_salv_rt");
        XisoReader.CopyOut(result.OutputPath, "/sub/nested.txt", Path.Combine(dest, "nested.txt"));
        Assert.Equal("nested"u8.ToArray(), File.ReadAllBytes(Path.Combine(dest, "nested.txt")));

        // The source is byte-identical: salvage only ever reads it.
        Assert.Equal(before, Sha256(isoPath));
    }

    [Fact]
    public void Salvage_ReservedBits_CarriedDespiteCorruptAttrs()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello"u8.ToArray()), "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        long header = FindEntryHeader(img, RootLayout(bad).RootAbs, RootLayout(bad).RootSize, "readme.txt");
        img[header + 12] |= Constants.AttributeReservedMask;
        File.WriteAllBytes(bad, img);
        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Reserved attribute bits", StringComparison.Ordinal));

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/readme.txt"], result.Copied);
    }

    [Fact]
    public void Salvage_MissingTag_CarriedOutputPasses()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello"u8.ToArray()), "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        using (FileStream fs = new(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength]);
        }

        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Optimized tag not found", StringComparison.Ordinal));

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/readme.txt"], result.Copied);
    }

    [Fact]
    public void Salvage_SeparatorName_SanitizedAndCarried()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "notex.txt"), "hello"u8.ToArray()), "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        (uint rootSector, uint rootSize, long rootAbs) = RootLayout(bad);
        _ = rootSector;
        long header = FindEntryHeader(img, rootAbs, rootSize, "notex.txt");
        img[header + 14 + 3] = (byte)'/';
        File.WriteAllBytes(bad, img);

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/not_x.txt"], result.Copied);
        Assert.True(XisoReader.AuditXiso(result.OutputPath).IsValid);
    }

    [Fact]
    public void Salvage_SeparatorCollision_DropsLoserWithReport()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "x_y.txt"), "winner"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "xqy.txt"), "loser"u8.ToArray());
        }, "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        (uint rootSector, uint rootSize, long rootAbs) = RootLayout(bad);
        _ = rootSector;
        long header = FindEntryHeader(img, rootAbs, rootSize, "xqy.txt");
        img[header + 14 + 1] = (byte)'/';
        File.WriteAllBytes(bad, img);

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/x_y.txt"], Sorted(result.Copied));
        Assert.Single(result.Skipped);
        Assert.Contains("separator-collision", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_ForgedFileSize_DropsEntryCarriesRest()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "good.txt"), "good"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "huge.txt"), "small"u8.ToArray());
        }, "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        (uint rootSector, uint rootSize, long rootAbs) = RootLayout(bad);
        _ = rootSector;
        long header = FindEntryHeader(img, rootAbs, rootSize, "huge.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 8), 0xFFFFFF00u);
        File.WriteAllBytes(bad, img);

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/good.txt"], result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("huge.txt", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_ForgedStartSector_DropsEntryCarriesRest()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "good.txt"), "good"u8.ToArray());
            File.WriteAllBytes(Path.Combine(src, "lost.txt"), "lost"u8.ToArray());
        }, "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        (uint rootSector, uint rootSize, long rootAbs) = RootLayout(bad);
        _ = rootSector;
        long header = FindEntryHeader(img, rootAbs, rootSize, "lost.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan((int)header + 4), 0xFFFFFF00u);
        File.WriteAllBytes(bad, img);

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/good.txt"], result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("lost.txt", result.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_TruncatedImage_DropsCutFileOutputPasses()
    {
        byte[] payload = new byte[100 * 1024];
        new Random(42).NextBytes(payload);
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "big.bin"), payload), "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        using (FileStream fs = new(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(fs.Length - 50000);
        }

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Empty(result.Copied);
        Assert.Single(result.Skipped);
        Assert.Contains("big.bin", result.Skipped[0], StringComparison.Ordinal);
        Assert.True(XisoReader.AuditXiso(result.OutputPath).IsValid);
    }

    [Fact]
    public void Salvage_RightSelfLoop_TerminatesCarriesReached()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        byte[] img = File.ReadAllBytes(bad);
        (uint rootSector, uint rootSize, long rootAbs) = RootLayout(bad);
        _ = rootSector;
        long header = FindEntryHeader(img, rootAbs, rootSize, "b.txt");
        ushort self = (ushort)((header - rootAbs) / 4);
        Assert.NotEqual(0, self);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan((int)header + 2), self);
        File.WriteAllBytes(bad, img);
        Assert.Contains(XisoReader.AuditXiso(bad).Issues,
            static i => i.Contains("Cycle detected", StringComparison.Ordinal));

        SalvageResult result = XisoReader.Salvage(bad);

        Assert.True(result.Success);
        Assert.Equal(["/a.txt", "/b.txt"], Sorted(result.Copied));
        Assert.Contains(result.Skipped, static s => s.Contains("Cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public void Salvage_DeepTree_TerminatesAtDepthGate()
    {
        string src = CreateTempDir("xiso_salv_deep");
        string dir = src;
        for (int i = 0; i < 70; i++)
        {
            dir = Path.Combine(dir, "a");
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(Path.Combine(dir, "leaf.txt"), "leaf\n");
        string outDir = CreateTempDir("xiso_salv_iso");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, "deep", null));
        Assert.NotNull(isoPath);

        SalvageResult result = XisoReader.Salvage(isoPath);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Copied, static p => p.EndsWith("leaf.txt", StringComparison.Ordinal));
        Assert.Contains(result.Skipped,
            static s => s.Contains("maximum directory depth", StringComparison.Ordinal));
    }

    [Fact]
    public void Salvage_BadMagic_ThrowsFormat()
    {
        string path = Path.Combine(CreateTempDir("xiso_salv_bad"), "garbage.iso");
        File.WriteAllBytes(path, "not an xbox image at all"u8.ToArray());
        Assert.Throws<XisoFormatException>(() => XisoReader.Salvage(path));
    }

    [Fact]
    public void Salvage_MissingFile_ThrowsNotFound() =>
        Assert.Throws<FileNotFoundException>(() =>
            XisoReader.Salvage(Path.Combine(CreateTempDir("xiso_salv_bad"), "no_such.iso")));

    [Fact]
    public void Salvage_EmptyPath_ThrowsArgument() => Assert.Throws<ArgumentException>(() => XisoReader.Salvage(""));

    [Fact]
    public void Salvage_RootBeyondLength_ThrowsFormat()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        string bad = CopyIso(isoPath, "xiso_salv_bad");
        long rootAbs = RootLayout(bad).RootAbs;
        using (FileStream fs = new(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(rootAbs - 100);
        }

        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.Salvage(bad));
        Assert.Contains("no tree root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Salvage_CsoInput_ProducesPlainIso()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllBytes(Path.Combine(src, "readme.txt"), "hello cso"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "n.txt"), "n"u8.ToArray());
        }, "game.iso");
        string csoDir = CreateTempDir("xiso_salv_cso");
        string csoPath = Path.Combine(csoDir, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath, level: 6));

        SalvageResult result = XisoReader.Salvage(csoPath);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(csoDir, "game.salvaged.iso"), result.OutputPath);
        Assert.Equal(["/readme.txt", "/sub/", "/sub/n.txt"], Sorted(result.Copied));
        VolumeInfo vol = XisoReader.GetVolumeInfo(result.OutputPath);
        Assert.True(vol.IsValid);
    }

    [Fact]
    public void Salvage_ExplicitOutput_Respected()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        string custom = Path.Combine(CreateTempDir("xiso_salv_custom"), "nested", "out.iso");

        SalvageResult result = XisoReader.Salvage(isoPath, custom);

        Assert.Equal(custom, result.OutputPath);
        Assert.True(File.Exists(custom));
        Assert.True(result.Success);
    }

    [Fact]
    public void Salvage_OutputResalvage_CarriesAll()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        SalvageResult first = XisoReader.Salvage(isoPath);
        Assert.True(first.Success);

        SalvageResult second = XisoReader.Salvage(first.OutputPath, first.OutputPath + ".round2.iso");

        Assert.True(second.Success);
        Assert.Empty(second.Skipped);
        Assert.Equal(Sorted(first.Copied), Sorted(second.Copied));
    }

    [Fact]
    public void SalvageCli_EndToEnd_WritesDefaultOutput()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(0, Program.Main(["--salvage", isoPath]));

        string expected = XisoSalvager.DefaultOutputPath(isoPath);
        Assert.True(File.Exists(expected));
        Assert.True(XisoReader.AuditXiso(expected).IsValid);
        Assert.Contains("PASS", _logCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SalvageCli_RepairOut_OverridesOutputPath()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        string custom = Path.Combine(CreateTempDir("xiso_salv_cli"), "custom.iso");

        Assert.Equal(0, Program.Main(["--salvage", "--repair-out", custom, isoPath]));
        Assert.True(File.Exists(custom));
    }

    [Fact]
    public void SalvageCli_TruncatedImage_ReportsDroppedAndPasses()
    {
        byte[] payload = new byte[100 * 1024];
        new Random(7).NextBytes(payload);
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "big.bin"), payload), "game.iso");
        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(fs.Length - 50000);
        }

        Assert.Equal(0, Program.Main(["--salvage", isoPath]));

        string log = _logCapture.ToString();
        Assert.Contains("Dropped", log, StringComparison.Ordinal);
        Assert.Contains("big.bin", log, StringComparison.Ordinal);
        Assert.Contains("PASS", log, StringComparison.Ordinal);
    }

    [Fact]
    public void SalvageCli_ExtractCombined_UsageError()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(1, Program.Main(["-x", "--salvage", isoPath]));
    }

    [Fact]
    public void SalvageCli_RepairOutWithoutSalvage_Rejected()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");

        Assert.Equal(1, Program.Main(["--repair-out", "out.iso", isoPath]));
    }

    [Fact]
    public void SalvageCli_MissingOperand_UsageError() => Assert.Equal(1, Program.Main(["--salvage"]));

    [Fact]
    public void SalvageCli_MissingFile_Fails() => Assert.Equal(1, Program.Main(["--salvage", Path.Combine(CreateTempDir("xiso_salv_cli"), "no.iso")]));

    [Fact]
    public void SalvageCli_ExistingOutput_AssumeNoRefusesAssumeYesOverwrites()
    {
        string isoPath = CreateIso(src =>
            File.WriteAllBytes(Path.Combine(src, "a.txt"), "hello"u8.ToArray()), "game.iso");
        string expected = XisoSalvager.DefaultOutputPath(isoPath);
        File.WriteAllText(expected, "sentinel");

        Assert.Equal(1, Program.Main(["--salvage", "-n", isoPath]));
        Assert.Equal("sentinel", File.ReadAllText(expected));

        Assert.Equal(0, Program.Main(["--salvage", "-y", isoPath]));
        Assert.NotEqual("sentinel", File.ReadAllText(expected), StringComparer.OrdinalIgnoreCase);
        Assert.True(XisoReader.AuditXiso(expected).IsValid);
    }
}
