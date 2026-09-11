using System.Buffers.Binary;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoRepairer"/> Phase 1 (TODO #26): in-place repair of
/// class-C issues — reserved attribute bits, missing optimized tag, and path
/// separators in filenames — plus refusal of truncation/structural/CISO/split
/// cases and the <c>--repair</c> CLI verb.
/// </summary>
[Collection("Sequential")]
public class XisoRepairTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly TextWriter _savedOut = Logger.Out;
    private readonly StringWriter _logCapture = new();

    public XisoRepairTests()
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

    private string CreateIsoWithFiles(params (string Name, byte[] Content)[] files)
    {
        string srcDir = CreateTempDir("xiso_repair_src");
        foreach ((string name, byte[] content) in files)
            File.WriteAllBytes(Path.Combine(srcDir, name), content);

        string outputDir = CreateTempDir("xiso_repair_out");
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    /// <summary>
    /// Locates the single occurrence of a filename in the raw image and returns
    /// its offset (the entry's attribute byte sits 2 bytes before it).
    /// </summary>
    private static int FindNameOffset(string isoPath, string fileName)
    {
        byte[] raw = File.ReadAllBytes(isoPath);
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(fileName);
        int found = -1;
        for (int i = 0; i + needle.Length <= raw.Length; i++)
        {
            if (raw.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                Assert.True(found < 0, $"filename '{fileName}' occurs more than once; test setup ambiguous");
                found = i;
            }
        }

        Assert.True(found >= 0, $"filename '{fileName}' not found in image");
        return found;
    }

    private static void PatchByte(string isoPath, long offset, byte value)
    {
        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.WriteByte(value);
    }

    /// <summary>
    /// Sets the reserved attribute bits of an entry's attribute byte, leaving
    /// every other bit (notably the directory bit) exactly as packed.
    /// </summary>
    private static void SetReservedBits(string isoPath, string fileName)
    {
        int attrOffset = FindNameOffset(isoPath, fileName) - 2;
        byte[] raw = File.ReadAllBytes(isoPath);
        PatchByte(isoPath, attrOffset, (byte)(raw[attrOffset] | Constants.AttributeReservedMask));
    }

    [Fact]
    public void Repair_ReservedBits_FixedAndMasked()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello world"u8.ToArray()));
        int attrOffset = FindNameOffset(isoPath, "readme.txt") - 2;

        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(attrOffset, SeekOrigin.Begin);
            byte raw = (byte)fs.ReadByte();
            fs.Seek(attrOffset, SeekOrigin.Begin);
            fs.WriteByte((byte)(raw | Constants.AttributeReservedMask));
        }

        Assert.Single(XisoReader.AuditXiso(isoPath).Issues);

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.True(result.Success);
        Assert.Single(result.Fixed);
        Assert.Empty(result.Remaining);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(isoPath + ".old"));

        // Backup preserves the corrupt byte; the image is now masked.
        Assert.NotEqual(0, File.ReadAllBytes(isoPath + ".old")[attrOffset] & Constants.AttributeReservedMask);
        Assert.Equal(0, File.ReadAllBytes(isoPath)[attrOffset] & Constants.AttributeReservedMask);
        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/readme.txt");
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Attributes & Constants.AttributeReservedMask);
    }

    [Fact]
    public void Repair_MissingTag_Fixed()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength], 0, Constants.OptimizedTagLength);
        }

        Assert.Contains(XisoReader.AuditXiso(isoPath).Issues,
            static i => i.Contains("Optimized tag", StringComparison.Ordinal));

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.True(result.Success);
        Assert.Single(result.Fixed);
        Assert.Contains("optimized tag", result.Fixed[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(XisoReader.IsOptimizedImage(isoPath));
    }

    [Fact]
    public void Repair_Separator_Renamed()
    {
        string isoPath = CreateIsoWithFiles(("qx.txt", "data"u8.ToArray()));
        PatchByte(isoPath, FindNameOffset(isoPath, "qx.txt"), (byte)'/');

        Assert.Contains(XisoReader.AuditXiso(isoPath).Issues,
            static i => i.Contains("path separator", StringComparison.Ordinal));

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.True(result.Success);
        Assert.Single(result.Fixed);
        Assert.NotNull(XisoReader.GetEntryInfo(isoPath, "/_x.txt"));
        Assert.Null(XisoReader.GetEntryInfo(isoPath, "/qx.txt"));
    }

    [Fact]
    public void Repair_SeparatorCollision_LeftRemaining()
    {
        string isoPath = CreateIsoWithFiles(
            ("qx.txt", "data"u8.ToArray()),
            ("_x.txt", "other"u8.ToArray()));
        PatchByte(isoPath, FindNameOffset(isoPath, "qx.txt"), (byte)'/');

        RepairResult result = XisoReader.Repair(isoPath);

        // The rename would collide with q_.txt: no fix, issue remains, and the
        // raw bytes are untouched (no backup — nothing was patched).
        Assert.False(result.Success);
        Assert.Empty(result.Fixed);
        Assert.Contains(result.Remaining,
            static i => i.Contains("path separator", StringComparison.Ordinal));
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(isoPath + ".old"));
    }

    [Fact]
    public void Repair_Truncation_Refused()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", new byte[8192]));
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(vol.IsValid);

        // Cut the image right after the root table: file data is gone, so the
        // entry's sector exceeds the file length (class T — unfixable).
        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(((long)vol.RootDirSector * Constants.SectorSize) + vol.RootDirSize);
        }

        Assert.NotEmpty(XisoReader.AuditXiso(isoPath).Issues);

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.False(result.Success);
        Assert.Empty(result.Fixed);
        Assert.NotEmpty(result.Remaining);
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public void Repair_BadMagic_Throws()
    {
        string junkFile = Path.Combine(CreateTempDir("xiso_repair_junk"), "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        Assert.Throws<XisoFormatException>(() => XisoReader.Repair(junkFile));
    }

    [Fact]
    public void Repair_MissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => XisoReader.Repair("no_such_file.iso"));

    [Fact]
    public void Repair_NullPath_Throws() => Assert.Throws<ArgumentException>(() => XisoReader.Repair(""));

    [Fact]
    public void Repair_Cso_Refused()
    {
        // Minimal CISO header (magic + size + DEFLATE version): the magic sniff
        // must refuse before the volume probe even runs.
        string dir = CreateTempDir("xiso_repair_cso");
        string csoPath = Path.Combine(dir, "fake.cso");
        byte[] hdr = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), CisoReader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), CisoReader.HeaderSize);
        hdr[20] = CisoWriter.VersionDeflate;
        File.WriteAllBytes(csoPath, hdr);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => XisoReader.Repair(csoPath));
        Assert.Contains("CISO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_SplitPart_Refused()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        string dir = Path.GetDirectoryName(isoPath)!;
        string partPath = Path.Combine(dir, "game.1.iso");
        File.Copy(isoPath, partPath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => XisoReader.Repair(partPath));
        Assert.Contains("split", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repair_IsIdempotent()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        SetReservedBits(isoPath, "readme.txt");

        RepairResult first = XisoReader.Repair(isoPath);
        Assert.True(first.Success);

        RepairResult second = XisoReader.Repair(isoPath);
        Assert.True(second.Success);
        Assert.Empty(second.Fixed);
    }

    [Fact]
    public void Repair_DryRun_ChangesNothing()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        SetReservedBits(isoPath, "readme.txt");
        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength], 0, Constants.OptimizedTagLength);
        }

        byte[] before = File.ReadAllBytes(isoPath);
        RepairResult result = XisoReader.Repair(isoPath, dryRun: true);

        Assert.True(result.DryRun);
        Assert.False(result.Success);
        Assert.Equal(2, result.Fixed.Count);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(isoPath + ".old"));
        Assert.Equal(before, File.ReadAllBytes(isoPath));
        Assert.NotEmpty(XisoReader.AuditXiso(isoPath).Issues);
    }

    [Fact]
    public void Repair_NoBackup_SkipsBackup()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        SetReservedBits(isoPath, "readme.txt");

        RepairResult result = XisoReader.Repair(isoPath, createBackup: false);

        Assert.True(result.Success);
        Assert.Single(result.Fixed);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(isoPath + ".old"));
    }

    [Fact]
    public void Repair_CleanImage_NoOp()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.True(result.Success);
        Assert.Empty(result.Fixed);
        Assert.Empty(result.Remaining);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(isoPath + ".old"));
    }

    [Fact]
    public void Repair_ConvergesThroughFixedDirectory()
    {
        // Reserved bits on both a subdirectory entry and a file inside it: the
        // first pass fixes the directory (unlocking its table) and the second
        // pass fixes the file. The collect walk never trusts the corrupt
        // directory pointer before its fix is decided.
        string srcDir = CreateTempDir("xiso_repair_conv_src");
        Directory.CreateDirectory(Path.Combine(srcDir, "qzsub"));
        File.WriteAllBytes(Path.Combine(srcDir, "qzsub", "qf.txt"), "data"u8.ToArray());
        string outputDir = CreateTempDir("xiso_repair_conv_out");
        Assert.Equal(0, XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);

        SetReservedBits(isoPath, "qzsub");
        SetReservedBits(isoPath, "qf.txt");
        Assert.Equal(2, XisoReader.AuditXiso(isoPath).Issues.Count);

        RepairResult result = XisoReader.Repair(isoPath);

        Assert.True(result.Success);
        Assert.Equal(2, result.Fixed.Count);
        Assert.NotNull(XisoReader.GetEntryInfo(isoPath, "/qzsub/qf.txt"));
    }

    [Fact]
    public void Cli_Repair_EndToEnd()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        SetReservedBits(isoPath, "readme.txt");

        Assert.Equal(0, Program.Main(["--repair", isoPath]));

        string output = _logCapture.ToString();
        Assert.Contains("Fixed:", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.True(File.Exists(isoPath + ".old"));
    }

    [Fact]
    public void Cli_Repair_DryRun_ChangesNothing()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));
        SetReservedBits(isoPath, "readme.txt");
        byte[] before = File.ReadAllBytes(isoPath);

        // Exit mirrors the audit: the issue remains (nothing was applied).
        Assert.Equal(1, Program.Main(["--repair", "--dry-run", isoPath]));

        string output = _logCapture.ToString();
        Assert.Contains("Would fix:", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.False(File.Exists(isoPath + ".old"));
        Assert.Equal(before, File.ReadAllBytes(isoPath));
    }

    [Fact]
    public void Cli_Repair_DryRun_CleanImage_Passes()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));

        Assert.Equal(0, Program.Main(["--repair", "--dry-run", isoPath]));
        Assert.Contains("PASS", _logCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Repair_Truncated_ReturnsOne()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", new byte[8192]));
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(((long)vol.RootDirSector * Constants.SectorSize) + vol.RootDirSize);
        }

        Assert.Equal(1, Program.Main(["--repair", isoPath]));
        Assert.Contains("FAIL", _logCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Repair_CombinedWithExtract_ReturnsOne()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));

        Assert.Equal(1, Program.Main(["-x", "--repair", isoPath]));
    }

    [Fact]
    public void Cli_Repair_MissingFile_ReturnsOne() => Assert.Equal(1, Program.Main(["--repair", "no_such_file.iso"]));

    [Fact]
    public void Cli_DryRun_WithoutRepair_ReturnsOne()
    {
        string isoPath = CreateIsoWithFiles(("readme.txt", "hello"u8.ToArray()));

        Assert.Equal(1, Program.Main(["--dry-run", isoPath]));
    }
}
