using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoReader.UnpackImage"/> — full-image extraction with
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiso_unpack_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), new string('B', 5000));
        return root;
    }

    private static string CreateIso(string srcDir, string isoName)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var isoPath = Path.Combine(outputDir, isoName);
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(isoPath, created);
        return isoPath;
    }

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

    [Fact]
    public void UnpackImage_NoOutputPath_ExtractsToIsoNamedDirectory()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var workDir = CreateTempDir();

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(workDir);

            var result = XisoReader.UnpackImage(isoPath);

            Assert.Equal(0, result);
            var target = Path.Combine(workDir, "game");
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
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");
        var dest = CreateTempDir();

        var result = XisoReader.UnpackImage(isoPath, dest);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_WithoutOptimizedTag_StillExtracts()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");

        // Wipe the optimized-tag marker so the image looks like a legacy (non-optimized)
        // dump; UnpackImage must probe the tag and fall back to llCompat mode.
        using (var fs = File.Open(isoPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            fs.Write(new byte[Constants.OptimizedTagLength]);
        }

        var dest = CreateTempDir();
        var result = XisoReader.UnpackImage(isoPath, dest);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_PrependedImage_WithSkipSectors_Extracts()
    {
        // The optimized-tag probe must shift with the skip offset (the writer places
        // the tag at prependOffset + 31337), so prepended images are detected correctly.
        var src = CreateSourceTree();
        var isoDir = Path.Combine(Path.GetTempPath(), $"xiso_unpack_pre_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isoDir);
        var isoPath = Path.Combine(isoDir, "prepended.iso");

        var createResult = XisoWriter.CreateXiso(src, isoDir, null, null, out _, "prepended.iso", null,
            prependSectors: 64);
        Assert.Equal(0, createResult);
        Assert.True(File.Exists(isoPath));

        var dest = CreateTempDir();
        var result = XisoReader.UnpackImage(isoPath, dest, skipSectors: 64);

        Assert.Equal(0, result);
        Assert.Equal(HashTree(src), HashTree(dest));
    }

    [Fact]
    public void UnpackImage_NegativeSkipSectors_Throws()
    {
        var src = CreateSourceTree();
        var isoPath = CreateIso(src, "game.iso");

        Assert.Throws<ArgumentOutOfRangeException>(() => XisoReader.UnpackImage(isoPath, skipSectors: -1));
    }

    [Fact]
    public void UnpackImage_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => XisoReader.UnpackImage("no_such_file.iso"));
    }

    [Fact]
    public void UnpackImage_InvalidIso_Throws()
    {
        var junkDir = CreateTempDir();
        var junkFile = Path.Combine(junkDir, "junk.iso");
        File.WriteAllBytes(junkFile, new byte[4096]);

        // A small invalid file is too short to probe the XGD offsets, so verification
        // fails with IOException; a full-size invalid image fails with XisoFormatException.
        // The working directory must be left unchanged (verification precedes the chdir).
        var originalCwd = Directory.GetCurrentDirectory();
        var ex = Record.Exception(() => XisoReader.UnpackImage(junkFile));
        Assert.True(ex is XisoFormatException or IOException,
            $"Expected XisoFormatException or IOException, got {(ex == null ? "no exception" : ex.GetType().Name)}");
        Assert.Equal(originalCwd, Directory.GetCurrentDirectory());
    }
}
