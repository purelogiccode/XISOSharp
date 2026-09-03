namespace XISOSharp.Tests;

/// <summary>
/// Integration tests for excluding files and directories during XISO creation
/// via <see cref="XisoWriter.CreateXiso"/> exclude patterns (extract-xiso issue #19).
/// </summary>
[Collection("Sequential")]
public class ExcludePatternsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RemoveSystemUpdate = false;

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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_excl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Creates a populated source tree and returns its path.</summary>
    private static string CreateSourceTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xiso_excl_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(root, "build"));
        Directory.CreateDirectory(Path.Combine(root, "$SystemUpdate"));

        File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(root, "skip.tmp"), "skip");
        File.WriteAllText(Path.Combine(root, "sub", "data.bin"), new string('D', 3000));
        File.WriteAllText(Path.Combine(root, "sub", "notes.tmp"), "temp");
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "js");
        File.WriteAllText(Path.Combine(root, "build", "out.o"), "obj");
        File.WriteAllText(Path.Combine(root, "$SystemUpdate", "update.bin"), "update");
        return root;
    }

    /// <summary>Extracts an ISO and returns the set of relative file paths (with '/').</summary>
    private static HashSet<string> ExtractFileSet(string isoPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"xiso_excl_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            var result = XisoReader.Extract(isoPath, extractDir, false);
            Assert.Equal(0, result);

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(extractDir, file).Replace('\\', '/'));
            }

            return files;
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    [Fact]
    public void CreateXiso_ExcludeFilePattern_OmitsMatchingFiles()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["**/*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.DoesNotContain("sub/notes.tmp", files);
    }

    [Fact]
    public void CreateXiso_ExcludeRootAnchoredPattern_KeepsNestedFiles()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        // "*.tmp" is anchored to the source root: only the top-level .tmp file is excluded.
        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.Contains("sub/notes.tmp", files);
    }

    [Fact]
    public void CreateXiso_ExcludeDirectoryPattern_SkipsEntireSubtree()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["**/node_modules/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("node_modules/pkg/index.js", files);

        // The excluded directory must not appear at all (not even as an empty dir).
        var extractDir = Path.Combine(Path.GetTempPath(), $"xiso_excl_probe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            XisoReader.Extract(isoPath, extractDir, false);
            Assert.False(Directory.Exists(Path.Combine(extractDir, "node_modules")),
                "Excluded directory should not exist in the image");
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    [Fact]
    public void CreateXiso_ExcludeRootOnly_KeepsNestedOccurrences()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        // Pattern anchored to the root: only the top-level "build" directory is excluded.
        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["build/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.DoesNotContain("build/out.o", files);
    }

    [Fact]
    public void CreateXiso_ExcludeSystemUpdate_AtAnyDepth()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["**/$SystemUpdate/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.DoesNotContain("$SystemUpdate/update.bin", files);
        Assert.Contains("keep.txt", files);
    }

    [Fact]
    public void CreateXiso_RemoveSystemUpdateFlag_ImplicitlyExcludes()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            var files = ExtractFileSet(isoPath);
            Assert.DoesNotContain("$SystemUpdate/update.bin", files);
            Assert.Contains("keep.txt", files);
        }
        finally
        {
            Logger.RemoveSystemUpdate = false;
        }
    }

    [Fact]
    public void CreateXiso_RemoveSystemUpdateFlag_MatchesExactNameOnly()
    {
        // Pins the create-side -s semantics: the implicit pattern **/$SystemUpdate/**
        // matches entries named exactly "$SystemUpdate" (unlike the extract-side check,
        // which uses substring matching). Names merely CONTAINING the string are kept.
        var src = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "$SystemUpdate"));
        Directory.CreateDirectory(Path.Combine(src, "my$SystemUpdateDir"));
        File.WriteAllText(Path.Combine(src, "$SystemUpdate", "update.bin"), "update");
        File.WriteAllText(Path.Combine(src, "my$SystemUpdateDir", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(src, "notes$SystemUpdate.txt"), "notes");
        var outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            // Clear the flag BEFORE extracting: this test pins the create-side semantics.
            // (The extract-side -s check uses substring matching and would skip these too.)
            Logger.RemoveSystemUpdate = false;

            var files = ExtractFileSet(isoPath);
            Assert.DoesNotContain("$SystemUpdate/update.bin", files);
            Assert.Contains("my$SystemUpdateDir/keep.txt", files);
            Assert.Contains("notes$SystemUpdate.txt", files);
        }
        finally
        {
            Logger.RemoveSystemUpdate = false;
        }
    }

    [Fact]
    public void CreateXiso_ExcludePatterns_CombinesWithFlag()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
                excludePatterns: ["**/*.tmp"]);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            var files = ExtractFileSet(isoPath);
            Assert.DoesNotContain("$SystemUpdate/update.bin", files);
            Assert.DoesNotContain("skip.tmp", files);
            Assert.DoesNotContain("sub/notes.tmp", files);
            Assert.Contains("sub/data.bin", files);
            Assert.Contains("node_modules/pkg/index.js", files);
        }
        finally
        {
            Logger.RemoveSystemUpdate = false;
        }
    }

    [Fact]
    public void CreateXiso_NoPatterns_IncludesEverything()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.Contains("skip.tmp", files);
        Assert.Contains("node_modules/pkg/index.js", files);
        Assert.Contains("$SystemUpdate/update.bin", files);
        Assert.Contains("build/out.o", files);
    }

    [Fact]
    public void CreateXiso_ExcludeAllFilesInDirectory_LeavesEmptyDirectory()
    {
        var src = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "empty_after"));
        Directory.CreateDirectory(Path.Combine(src, "full"));
        File.WriteAllText(Path.Combine(src, "full", "a.tmp"), "a");
        File.WriteAllText(Path.Combine(src, "full", "b.tmp"), "b");
        File.WriteAllText(Path.Combine(src, "keep.txt"), "keep");
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(src, outputDir, null, null, out var isoPath, null, null,
            excludePatterns: ["**/*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var extractDir = CreateTempDir();
        XisoReader.Extract(isoPath, extractDir, false);

        // The directory itself does not match "*.tmp", so it stays (as an empty dir).
        Assert.True(Directory.Exists(Path.Combine(extractDir, "full")));
        Assert.True(Directory.Exists(Path.Combine(extractDir, "empty_after")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(extractDir, "full")));
        Assert.True(File.Exists(Path.Combine(extractDir, "keep.txt")));
    }

    [Fact]
    public async Task CreateXisoAsync_ExcludePatterns_OmitsMatchingFiles()
    {
        var src = CreateSourceTree();
        var outputDir = CreateTempDir();

        (var result, var isoPath) = await XisoWriter.CreateXisoAsync(
            src, outputDir, null, null, null, null, excludePatterns: ["**/node_modules/**", "*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.DoesNotContain("node_modules/pkg/index.js", files);
    }
}