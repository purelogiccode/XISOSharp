using System.Security.Cryptography;
using System.Text;
using XISOSharp.Models;

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

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_skip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void PopulateSourceDir(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "file1.txt"), "hello world");
        File.WriteAllText(Path.Combine(dir, "file2.txt"), new string('A', 5000)); // spans multiple sectors
        byte[] binary = new byte[7000];
        new Random(42).NextBytes(binary);
        File.WriteAllBytes(Path.Combine(dir, "subdir", "data.bin"), binary);
    }

    /// <summary>Returns relative path → SHA-256 hex for every file under <paramref name="root"/>.</summary>
    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using SHA256 sha = SHA256.Create();
            using FileStream fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    private static string CreatePrependedIso(string srcDir, string outputDir)
    {
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null,
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
        using FileStream fs = File.OpenRead(isoPath);
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
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();

        string isoPath = CreatePrependedIso(srcDir, outputDir);

        const long prependOffset = (long)PrependSectors * Constants.SectorSize;
        long fileLength = new FileInfo(isoPath).Length;

        // The whole image (placeholder + game partition) is 64 KB aligned.
        Assert.True(fileLength > prependOffset);
        Assert.Equal(0, (fileLength - prependOffset) % Constants.FileModulus);

        using FileStream fs = File.OpenRead(isoPath);

        // The placeholder region is zero-filled.
        byte[] placeholder = new byte[prependOffset];
        Assert.Equal(prependOffset, fs.Read(placeholder, 0, (int)prependOffset));
        Assert.All(placeholder, b => Assert.Equal(0, b));

        // Header magic lives at prependOffset + HeaderOffset.
        fs.Seek(prependOffset + Constants.HeaderOffset, SeekOrigin.Begin);
        byte[] magic = new byte[Constants.HeaderDataLength];
        fs.ReadExactly(magic);
        Assert.Equal(Constants.HeaderData, Encoding.ASCII.GetString(magic));

        // ECMA-119 volume descriptors also shifted.
        fs.Seek(prependOffset + Constants.Ecma119DataAreaStart, SeekOrigin.Begin);
        Assert.Equal(0x01, fs.ReadByte());
        byte[] cd001 = new byte[5];
        fs.ReadExactly(cd001);
        Assert.Equal("CD001", Encoding.ASCII.GetString(cd001));
    }

    [Fact]
    public void VerifyXiso_WithSkipSectors_DetectsPrependedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        using FileStream fs = File.OpenRead(isoPath);
        (uint rootDirSector, uint rootDirSize, long discLseek) =
            XisoReader.VerifyXiso(fs, "prepended.iso", PrependSectors);

        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal((long)PrependSectors * Constants.SectorSize, discLseek);
    }

    [Fact]
    public void VerifyXiso_WithoutSkipSectors_ThrowsOnPrependedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        AssertNotReadableWithoutSkip(isoPath);
    }

    [Fact]
    public void VerifyXiso_WrongSkipOffset_Throws()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        using FileStream fs = File.OpenRead(isoPath);
        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(fs, "prepended.iso", PrependSectors + 1));
    }

    [Fact]
    public void VerifyXiso_NegativeSkipSectors_Throws()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        using FileStream fs = File.OpenRead(isoPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.VerifyXiso(fs, "prepended.iso", -1));
    }

    [Fact]
    public void CreateXiso_NegativePrependSectors_Throws()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoWriter.CreateXiso(srcDir, outputDir, null, null, out _, null, null, prependSectors: -5));
    }

    [Fact]
    public void CreateXiso_PrependThenExtractSkip_RoundTripsFileContents()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        string extractDir = CreateTempDir();
        int extractResult = XisoReader.Extract(isoPath, extractDir, false, skipSectors: PrependSectors);

        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Extract_WithoutSkip_OnPrependedIso_Throws()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        AssertNotReadableWithoutSkip(isoPath);

        string extractDir = CreateTempDir();
        Exception? ex = Record.Exception(() => XisoReader.Extract(isoPath, extractDir, false));
        Assert.True(ex is XisoFormatException or IOException,
            $"Expected XisoFormatException or IOException, got {(ex == null ? "no exception" : ex.GetType().Name)}");
    }

    [Fact]
    public void List_WithSkipSectors_ListsPrependedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        int listResult = XisoReader.List(isoPath, false, skipSectors: PrependSectors);
        Assert.Equal(0, listResult);
    }

    [Fact]
    public void Tree_WithSkipSectors_ListsPrependedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        int treeResult = XisoReader.Tree(isoPath, false, skipSectors: PrependSectors);
        Assert.Equal(0, treeResult);
    }

    [Fact]
    public void Rewrite_WithPrependSectors_ProducesPrependedOptimizedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string createDir = CreateTempDir();
        int createResult = XisoWriter.CreateXiso(srcDir, createDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string rewriteDir = CreateTempDir();
        int rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath,
            prependSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath));

        // The rewritten image must be readable only when the skip offset is supplied.
        AssertNotReadableWithoutSkip(rewrittenPath);

        string extractDir = CreateTempDir();
        int extractResult = XisoReader.Extract(rewrittenPath, extractDir, false, skipSectors: PrependSectors);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Rewrite_WithSkipSectors_ReadsOffsetSource()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        // Rewrite reads the source at the skip offset and produces a normal (unshifted) ISO.
        string rewriteDir = CreateTempDir();
        int rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath,
            skipSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);

        string extractDir = CreateTempDir();
        int extractResult = XisoReader.Extract(rewrittenPath, extractDir, false);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void Rewrite_WithSkipAndPrepend_RoundTripsRedumpStyle()
    {
        // Source is a Redump-style image (game partition after a placeholder).
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        // Rewrite: read at the skip offset, write back with the same prepend.
        string rewriteDir = CreateTempDir();
        int rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath,
            skipSectors: PrependSectors, prependSectors: PrependSectors);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath));

        // The rewritten image keeps the offset layout and remains readable with skip.
        AssertNotReadableWithoutSkip(rewrittenPath);

        string extractDir = CreateTempDir();
        int extractResult = XisoReader.Extract(rewrittenPath, extractDir, false, skipSectors: PrependSectors);
        Assert.Equal(0, extractResult);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public void CreateXiso_PrependZero_BehavesLikeNormalIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null,
            prependSectors: 0);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        using FileStream fs = File.OpenRead(isoPath);
        (uint rootDirSector, uint rootDirSize, long discLseek) = XisoReader.VerifyXiso(fs, "normal.iso");
        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal(0, discLseek);
    }

    [Fact]
    public async Task DecodeXisoAsync_WithSkipSectors_ExtractsPrependedIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();
        string isoPath = CreatePrependedIso(srcDir, outputDir);

        string extractDir = CreateTempDir();
        (int result, _) = await XisoReader.DecodeXisoAsync(isoPath, extractDir, ExtractMode.Extract,
            llCompat: false, skipSectors: PrependSectors);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(srcDir), HashTree(extractDir));
    }

    [Fact]
    public async Task CreateXisoAsync_WithPrependSectors_ProducesOffsetIso()
    {
        string srcDir = CreateTempDir();
        PopulateSourceDir(srcDir);
        string outputDir = CreateTempDir();

        (int result, string? isoPath) = await XisoWriter.CreateXisoAsync(
            srcDir, outputDir, null, null, null, null, prependSectors: PrependSectors);

        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));

        await using FileStream fs = File.OpenRead(isoPath);
        (_, _, long discLseek) = XisoReader.VerifyXiso(fs, "async.iso", PrependSectors);
        Assert.Equal((long)PrependSectors * Constants.SectorSize, discLseek);
    }
}
