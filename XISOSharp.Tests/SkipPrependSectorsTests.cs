using System.Security.Cryptography;
using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for skip/prepend sector support (extract-xiso issue #33): reading XISO images
/// whose game partition does not start at file offset 0 (Redump-style images with a
/// video partition), and writing images with room prepended for such a partition.
/// </summary>
[Collection("Sequential")]
public class SkipPrependSectorsTests : IDisposable
{
    private const int PrependSectors = 64;

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
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_skip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void PopulateSourceDir(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello world");
        File.WriteAllText(Path.Combine(dir, "file2.txt"), new string('A', 5000)); // spans multiple sectors
        var binary = new byte[7000];
        new Random(42).NextBytes(binary);
        File.WriteAllBytes(Path.Combine(dir, "subdir", "data.bin"), binary);
    }

    /// <summary>Returns relative path → SHA-256 hex for every file under <paramref name="root"/>.</summary>
    private static Dictionary<string, string> HashTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    private static string CreatePrependedIso(string srcDir, string outputDir)
    {
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null,
            prependSectors: PrependSectors);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
        return isoPath;
    }

    /// <summary>
    /// A small prepended image is too short to probe the XGD offsets, so verification fails
    /// with <see cref="IOException"/>; a full-size (Redump-scale) image fails with
    /// <see cref="XisoFormatException"/>. Both mean "not readable without a skip offset".
    /// </summary>
    private static void AssertNotReadableWithoutSkip(string isoPath)
    {
        using var fs = File.OpenRead(isoPath);
        Exception? ex = null;
        try
        {
            XisoReader.VerifyXiso(fs, "prepended.iso");
        }
        catch (Exception e)
        {
            ex = e;
        }

        Assert.True(ex is XisoFormatException or IOException,
            $"Expected XisoFormatException or IOException, got {(ex == null ? "no exception" : ex.GetType().Name)}");
    }

    [Fact]
    public void CreateXiso_PrependSectors_ShiftsHeaderAndData()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();

        var isoPath = CreatePrependedIso(srcDir, outputDir);

        const long prependOffset = (long)PrependSectors * Constants.SectorSize;
        var fileLength = new FileInfo(isoPath).Length;

        // The whole image (placeholder + game partition) is 64 KB aligned.
        Assert.True(fileLength > prependOffset);
        Assert.Equal(0, (fileLength - prependOffset) % Constants.FileModulus);

        using var fs = File.OpenRead(isoPath);

        // The placeholder region is zero-filled.
        var placeholder = new byte[prependOffset];
        Assert.Equal(prependOffset, fs.Read(placeholder, 0, (int)prependOffset));
        Assert.All(placeholder, b => Assert.Equal(0, b));

        // Header magic lives at prependOffset + HeaderOffset.
        fs.Seek(prependOffset + Constants.HeaderOffset, SeekOrigin.Begin);
        var magic = new byte[Constants.HeaderDataLength];
        fs.ReadExactly(magic);
        Assert.Equal(Constants.HeaderData, Encoding.ASCII.GetString(magic));

        // ECMA-119 volume descriptors also shifted.
        fs.Seek(prependOffset + Constants.Ecma119DataAreaStart, SeekOrigin.Begin);
        Assert.Equal(0x01, fs.ReadByte());
        var cd001 = new byte[5];
        fs.ReadExactly(cd001);
        Assert.Equal("CD001", Encoding.ASCII.GetString(cd001));
    }

    [Fact]
    public void VerifyXiso_WithSkipSectors_DetectsPrependedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        using var fs = File.OpenRead(isoPath);
        (var rootDirSector, var rootDirSize, var discLseek) =
            XisoReader.VerifyXiso(fs, "prepended.iso", PrependSectors);

        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal((long)PrependSectors * Constants.SectorSize, discLseek);
    }

    [Fact]
    public void VerifyXiso_WithoutSkipSectors_ThrowsOnPrependedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        AssertNotReadableWithoutSkip(isoPath);
    }

    [Fact]
    public void VerifyXiso_WrongSkipOffset_Throws()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        using var fs = File.OpenRead(isoPath);
        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(fs, "prepended.iso", PrependSectors + 1));
    }

    [Fact]
    public void VerifyXiso_NegativeSkipSectors_Throws()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        using var fs = File.OpenRead(isoPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.VerifyXiso(fs, "prepended.iso", -1));
    }

    [Fact]
    public void CreateXiso_NegativePrependSectors_Throws()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoWriter.CreateXiso(srcDir, outputDir, null, null, out _, null, null, prependSectors: -5));
    }

    [Fact]
    public void CreateXiso_PrependThenExtractSkip_RoundTripsFileContents()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        var extractDir = CreateTempDir();
        var extractResult = XisoReader.Extract(isoPath, extractDir, false, skipSectors: PrependSectors);

        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Extract_WithoutSkip_OnPrependedIso_Throws()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        AssertNotReadableWithoutSkip(isoPath);

        var extractDir = CreateTempDir();
        var ex = Record.Exception(() => XisoReader.Extract(isoPath, extractDir, false));
        Assert.True(ex is XisoFormatException or IOException,
            $"Expected XisoFormatException or IOException, got {(ex == null ? "no exception" : ex.GetType().Name)}");
    }

    [Fact]
    public void List_WithSkipSectors_ListsPrependedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        var listResult = XisoReader.List(isoPath, false, skipSectors: PrependSectors);
        Assert.Equal(0, listResult);
    }

    [Fact]
    public void Tree_WithSkipSectors_ListsPrependedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        var treeResult = XisoReader.Tree(isoPath, false, skipSectors: PrependSectors);
        Assert.Equal(0, treeResult);
    }

    [Fact]
    public void Rewrite_WithPrependSectors_ProducesPrependedOptimizedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var createDir = CreateTempDir();
        var createResult = XisoWriter.CreateXiso(srcDir, createDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var rewriteDir = CreateTempDir();
        var rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath,
            prependSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath));

        // The rewritten image must be readable only when the skip offset is supplied.
        AssertNotReadableWithoutSkip(rewrittenPath);

        var extractDir = CreateTempDir();
        var extractResult = XisoReader.Extract(rewrittenPath, extractDir, false, skipSectors: PrependSectors);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Rewrite_WithSkipSectors_ReadsOffsetSource()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        // Rewrite reads the source at the skip offset and produces a normal (unshifted) ISO.
        var rewriteDir = CreateTempDir();
        var rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath,
            skipSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);

        var extractDir = CreateTempDir();
        var extractResult = XisoReader.Extract(rewrittenPath, extractDir, false);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Rewrite_WithSkipAndPrepend_RoundTripsRedumpStyle()
    {
        // Source is a Redump-style image (game partition after a placeholder).
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        // Rewrite: read at the skip offset, write back with the same prepend.
        var rewriteDir = CreateTempDir();
        var rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath,
            skipSectors: PrependSectors, prependSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath));

        // The rewritten image keeps the offset layout and remains readable with skip.
        AssertNotReadableWithoutSkip(rewrittenPath);

        var extractDir = CreateTempDir();
        var extractResult = XisoReader.Extract(rewrittenPath, extractDir, false, skipSectors: PrependSectors);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void CreateXiso_PrependZero_BehavesLikeNormalIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null,
            prependSectors: 0);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        using var fs = File.OpenRead(isoPath);
        (var rootDirSector, var rootDirSize, var discLseek) = XisoReader.VerifyXiso(fs, "normal.iso");
        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal(0, discLseek);
    }

    [Fact]
    public async Task DecodeXisoAsync_WithSkipSectors_ExtractsPrependedIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();
        var isoPath = CreatePrependedIso(srcDir, outputDir);

        var extractDir = CreateTempDir();
        (var result, _) = await XisoReader.DecodeXisoAsync(isoPath, extractDir, ExtractMode.Extract,
            llCompat: false, skipSectors: PrependSectors);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public async Task CreateXisoAsync_WithPrependSectors_ProducesOffsetIso()
    {
        var srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        var outputDir = CreateTempDir();

        (var result, var isoPath) = await XisoWriter.CreateXisoAsync(
            srcDir, outputDir, null, null, null, null, prependSectors: PrependSectors);

        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));

        await using var fs = File.OpenRead(isoPath);
        (_, _, var discLseek) = XisoReader.VerifyXiso(fs, "async.iso", PrependSectors);
        Assert.Equal((long)PrependSectors * Constants.SectorSize, discLseek);
    }
}