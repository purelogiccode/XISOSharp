using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.UnpackImage(string, string?, CancellationToken, int?, XISOSharp.UnpackOptions?, IProgress{XISOSharp.Models.ProgressInfo}?)"/> — full-image extraction with
/// automatic optimized-tag detection and ISO-named default output directory.
/// </summary>
[Collection("Sequential")]
public class UnpackImageTests : IDisposable
{
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateSourceTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xiso_unpack_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), new string('B', 5000));
        return root;
    }

    private static string CreateIso(string srcDir, string isoName)
    {
        string outputDir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        string isoPath = Path.Combine(outputDir, isoName);
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(isoPath, created);
        return isoPath;
    }

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

    [Fact]
    public void UnpackImage_NoOutputPath_ExtractsToIsoNamedDirectory()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string workDir = CreateTempDir();

        string originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(workDir);

            int result = XisoReader.UnpackImage(isoPath);

            Assert.Equal(0, result);
            string target = Path.Combine(workDir, "game");
            Assert.True(Directory.Exists(target), $"expected ISO-named directory {target}");
            Assert.Equal(HashTree(src), HashTree(target));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void UnpackImage_WithOutputPath_ExtractsToDirectory()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string dest = CreateTempDir();

        int result = XisoReader.UnpackImage(isoPath, dest);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_WithoutOptimizedTag_StillExtracts()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");

        // Wipe the optimized-tag marker so the image looks like a legacy (non-optimized)
        // dump; UnpackImage must probe the tag and fall back to llCompat mode.
        using (FileStream fs = File.Open(isoPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength]);
        }

        string dest = CreateTempDir();
        int result = XisoReader.UnpackImage(isoPath, dest);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_PrependedImage_WithSkipSectors_Extracts()
    {
        // The optimized-tag probe must shift with the skip offset (the writer places
        // the tag at prependOffset + 31337), so prepended images are detected correctly.
        string src = CreateSourceTree();
        string isoDir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_pre_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isoDir);
        string isoPath = Path.Combine(isoDir, "prepended.iso");

        int createResult = XisoWriter.CreateXiso(src, isoDir, null, null, out _, "prepended.iso", null,
            prependSectors: 64);
        Assert.Equal(0, createResult);
        Assert.True(File.Exists(isoPath));

        string dest = CreateTempDir();
        int result = XisoReader.UnpackImage(isoPath, dest, skipSectors: 64);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_NegativeSkipSectors_Throws()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");

        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.UnpackImage(isoPath, skipSectors: -1));
    }

    private sealed class CancelOnFirstFile(CancellationTokenSource cts) : IProgress<ProgressInfo>
    {
        private readonly CancellationTokenSource _cts = cts;

        public void Report(ProgressInfo info)
        {
            if (info.Type == ProgressInfoType.FileAdded)
                _cts.Cancel();
        }
    }

    [Fact]
    public void Rewrite_CancelDuringWritePhase_Aborts()
    {
        // BUG-LIB-022: the rewrite write phase observes the token — cancelling
        // on the first written file aborts the run instead of finishing it.
        string src = CreateSourceTree();
        string isoPath = CreateIso(src, "game.iso");
        string outDir = CreateTempDir();
        using CancellationTokenSource cts = new();
        CancelOnFirstFile progress = new(cts);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            XisoReader.DecodeXiso(isoPath, outDir, ExtractMode.Rewrite, out _, true,
                cancellationToken: cts.Token, progress: progress));
    }

    [Fact]
    public void UnpackImage_MissingFile_Throws() => Assert.Throws<FileNotFoundException>(() => XisoReader.UnpackImage("no_such_file.iso"));

    [Fact]
    public void Rewrite_PrependedImage_IgnoresStaleGlobalLseek()
    {
        // BUG-LIB-013: the writer must use the rewrite's own disc lseek (passed
        // explicitly), never the Logger.XboxDiscLseek legacy mirror. Poison the
        // global: a prepended image (nonzero lseek) must still rewrite byte-true.
        string src = CreateSourceTree();
        string isoDir = CreateTempDir();
        int createResult = XisoWriter.CreateXiso(src, isoDir, null, null, out _, "prepended.iso", null,
            prependSectors: 64);
        Assert.Equal(0, createResult);
        string isoPath = Path.Combine(isoDir, "prepended.iso");

        long savedLseek = Logger.XboxDiscLseek;
        Logger.XboxDiscLseek = 0x0BAD_F00D;
        try
        {
            string outDir = CreateTempDir();
            int rc = XisoReader.DecodeXiso(isoPath, outDir, ExtractMode.Rewrite, out string? rewritten, true,
                skipSectors: 64);
            Assert.Equal(0, rc);
            Assert.NotNull(rewritten);

            Assert.True(XisoReader.AuditXiso(rewritten).IsValid);
            string dest = CreateTempDir();
            Assert.Equal(0, XisoReader.UnpackImage(rewritten, dest));
            Assert.Equal(HashTree(src), HashTree(dest));
        }
        finally
        {
            Logger.XboxDiscLseek = savedLseek;
        }
    }

    [Fact]
    public void UnpackImage_InvalidIso_Throws()
    {
        string junkDir = CreateTempDir();
        string junkFile = Path.Combine(junkDir, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        // A small invalid file is too short to probe the XGD offsets, so verification
        // fails with IOException; a full-size invalid image fails with XisoFormatException.
        // The working directory must be left unchanged (verification precedes the chdir).
        string originalCwd = Directory.GetCurrentDirectory();
        Exception? ex = Record.Exception(() => XisoReader.UnpackImage(junkFile));
        Assert.True(ex is XisoFormatException or IOException,
            $"Expected XisoFormatException or IOException, got {(ex == null ? "no exception" : ex.GetType().Name)}");
        Assert.Equal(originalCwd, Directory.GetCurrentDirectory());
    }
}
