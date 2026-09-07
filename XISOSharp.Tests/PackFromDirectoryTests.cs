using System.Security.Cryptography;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoWriter.PackFromDirectory"/> — directory-to-ISO packing
/// with a 1:1 mapping (the library behind the CLI's --pack flag).
/// </summary>
[Collection("Sequential")]
public class PackFromDirectoryTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_pack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiso_pack_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "sub", "b.bin"), new string('B', 5000));
        File.WriteAllText(Path.Combine(root, "skip.tmp"), "temp");
        File.WriteAllText(Path.Combine(root, "node_modules", "x.js"), "js");
        return root;
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

    private static string ExtractToTemp(string isoPath)
    {
        var dest = Path.Combine(Path.GetTempPath(), $"xiso_pack_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        var result = XisoReader.Extract(isoPath, dest, false);
        Assert.Equal(0, result);
        return dest;
    }

    [Fact]
    public void PackFromDirectory_CreatesIso_With1To1Mapping()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var isoPath = Path.Combine(outputDir, "packed.iso");

        var result = XisoWriter.PackFromDirectory(src, isoPath);
        Assert.Equal(0, result);
        Assert.True(File.Exists(isoPath));

        var extracted = ExtractToTemp(isoPath);
        Assert.Equal(HashTree(src), HashTree(extracted));
    }

    [Fact]
    public void PackFromDirectory_OutputPathMayIncludeSubdirectory()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var nested = Path.Combine(outputDir, "nested");
        var isoPath = Path.Combine(nested, "packed.iso");

        var result = XisoWriter.PackFromDirectory(src, isoPath);
        Assert.Equal(0, result);
        Assert.True(File.Exists(isoPath));

        var extracted = ExtractToTemp(isoPath);
        Assert.Equal(HashTree(src), HashTree(extracted));
    }

    [Fact]
    public void PackFromDirectory_ExcludePatterns_AreHonored()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var isoPath = Path.Combine(outputDir, "packed.iso");

        var result = XisoWriter.PackFromDirectory(src, isoPath,
            excludePatterns: ["**/*.tmp", "**/node_modules/**"]);
        Assert.Equal(0, result);

        var extracted = ExtractToTemp(isoPath);
        var files = HashTree(extracted);
        Assert.Contains("a.txt", files.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sub/b.bin", files.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("skip.tmp", files.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("node_modules/x.js", files.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackFromDirectory_MissingSource_Throws()
    {
        var outputDir = CreateTempDir();
        var isoPath = Path.Combine(outputDir, "packed.iso");

        Assert.Throws<DirectoryNotFoundException>(() =>
            XisoWriter.PackFromDirectory(Path.Combine(outputDir, "nope"), isoPath));
    }

    [Fact]
    public void PackFromDirectory_NullOrEmptyOutputPath_Throws()
    {
        var src = CreateSourceTree();

        Assert.Throws<ArgumentException>(() => XisoWriter.PackFromDirectory(src, ""));
        Assert.Throws<ArgumentNullException>(() => XisoWriter.PackFromDirectory(src, null!));
    }

    [Fact]
    public async Task PackFromDirectoryAsync_CreatesIso()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();
        var isoPath = Path.Combine(outputDir, "async.iso");

        var result = await XisoWriter.PackFromDirectoryAsync(src, isoPath);
        Assert.Equal(0, result);
        Assert.True(File.Exists(isoPath));

        var extracted = ExtractToTemp(isoPath);
        Assert.Equal(HashTree(src), HashTree(extracted));
    }

    [Fact]
    public void PackFromDirectory_RestoresCwd_OnSuccess()
    {
        // The create path hops CWD internally; it must not leak (#22).
        var before = Directory.GetCurrentDirectory();
        try
        {
            var src = CreateSourceTree();
            _tempDirs.Add(src);
            var isoPath = Path.Combine(CreateTempDir(), "packed.iso");

            Assert.Equal(0, XisoWriter.PackFromDirectory(src, isoPath));
            Assert.Equal(before, Directory.GetCurrentDirectory());
        }
        finally
        {
            try
            {
                Directory.SetCurrentDirectory(before);
            }
            catch
            {
                // ignored: assertion above already reports the leak
            }
        }
    }

    [Fact]
    public void CreateXiso_RestoresCwd_WhenOutputCollides()
    {
        // The #55 collision throws *after* chdir into the source; CWD must
        // still be restored (#22).
        var before = Directory.GetCurrentDirectory();
        var src = CreateSourceTree();
        _tempDirs.Add(src);
        try
        {
            var leaf = Path.GetFileName(src);
            var parent = Path.GetDirectoryName(src)!;
            Assert.Throws<ArgumentException>(() =>
                XisoWriter.CreateXiso(src, parent, null, null, out _, leaf, null));
            Assert.Equal(before, Directory.GetCurrentDirectory());
        }
        finally
        {
            try
            {
                Directory.SetCurrentDirectory(before);
            }
            catch
            {
                // ignored: assertion above already reports the leak
            }
        }
    }

    [Fact]
    public async Task PackFromDirectory_ConcurrentCalls_AllSucceed()
    {
        // Creates are serialized process-wide (#22) — parallel packs must
        // all succeed with intact outputs instead of corrupting shared CWD.
        var before = Directory.GetCurrentDirectory();
        var cases = Enumerable.Range(0, 4).Select(_ =>
        {
            var src = CreateSourceTree();
            _tempDirs.Add(src);
            var iso = Path.Combine(CreateTempDir(), "packed.iso");
            return (src, iso);
        }).ToList();

        var tasks = cases.Select(c => Task.Run(() => XisoWriter.PackFromDirectory(c.src, c.iso))).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var (result, (src, iso)) in results.Zip(cases))
        {
            Assert.Equal(0, result);
            var extracted = ExtractToTemp(iso);
            Assert.Equal(HashTree(src), HashTree(extracted));
        }

        Assert.Equal(before, Directory.GetCurrentDirectory());
    }
}