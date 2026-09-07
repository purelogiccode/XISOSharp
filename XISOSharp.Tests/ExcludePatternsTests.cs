namespace XISOSharp.Tests;

/// <summary>
/// Integration tests for excluding files and directories during XISO creation
/// via <see cref="XisoWriter.CreateXiso"/> exclude patterns (extract-xiso issue #19).
/// </summary>
[Collection("Sequential")]
public class ExcludePatternsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly bool _savedRemoveSystemUpdate;

    public ExcludePatternsTests()
    {
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        _savedRemoveSystemUpdate = Logger.RemoveSystemUpdate;
    }

    public void Dispose()
    {
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        Logger.RemoveSystemUpdate = _savedRemoveSystemUpdate;

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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_excl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Creates a populated source tree and returns its path.</summary>
    private static string CreateSourceTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xiso_excl_src_{Guid.NewGuid():N}");
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
        string extractDir = Path.Combine(Path.GetTempPath(), $"xiso_excl_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            int result = XisoReader.Extract(isoPath, extractDir, false);
            Assert.Equal(0, result);

            HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
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
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["**/*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.DoesNotContain("sub/notes.tmp", files);
    }

    [Fact]
    public void CreateXiso_ExcludeRootAnchoredPattern_KeepsNestedFiles()
    {
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        // "*.tmp" is anchored to the source root: only the top-level .tmp file is excluded.
        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.Contains("sub/notes.tmp", files);
    }

    [Fact]
    public void CreateXiso_ExcludeDirectoryPattern_SkipsEntireSubtree()
    {
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["**/node_modules/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("node_modules/pkg/index.js", files);

        // The excluded directory must not appear at all (not even as an empty dir).
        string extractDir = Path.Combine(Path.GetTempPath(), $"xiso_excl_probe_{Guid.NewGuid():N}");
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
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        // Pattern anchored to the root: only the top-level "build" directory is excluded.
        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["build/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.DoesNotContain("build/out.o", files);
    }

    [Fact]
    public void CreateXiso_ExcludeSystemUpdate_AtAnyDepth()
    {
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["**/$SystemUpdate/**"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.DoesNotContain("$SystemUpdate/update.bin", files);
        Assert.Contains("keep.txt", files);
    }

    [Fact]
    public void CreateXiso_RemoveSystemUpdateFlag_ImplicitlyExcludes()
    {
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            HashSet<string> files = ExtractFileSet(isoPath);
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
        string src = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "$SystemUpdate"));
        Directory.CreateDirectory(Path.Combine(src, "my$SystemUpdateDir"));
        File.WriteAllText(Path.Combine(src, "$SystemUpdate", "update.bin"), "update");
        File.WriteAllText(Path.Combine(src, "my$SystemUpdateDir", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(src, "notes$SystemUpdate.txt"), "notes");
        string outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            // Clear the flag BEFORE extracting: this test pins the create-side semantics.
            // (The extract-side -s check uses substring matching and would skip these too.)
            Logger.RemoveSystemUpdate = false;

            HashSet<string> files = ExtractFileSet(isoPath);
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
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        Logger.RemoveSystemUpdate = true;
        try
        {
            int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
                excludePatterns: ["**/*.tmp"]);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            HashSet<string> files = ExtractFileSet(isoPath);
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
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.Contains("skip.tmp", files);
        Assert.Contains("node_modules/pkg/index.js", files);
        Assert.Contains("$SystemUpdate/update.bin", files);
        Assert.Contains("build/out.o", files);
    }

    [Fact]
    public void CreateXiso_ExcludeAllFilesInDirectory_LeavesEmptyDirectory()
    {
        string src = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(src, "empty_after"));
        Directory.CreateDirectory(Path.Combine(src, "full"));
        File.WriteAllText(Path.Combine(src, "full", "a.tmp"), "a");
        File.WriteAllText(Path.Combine(src, "full", "b.tmp"), "b");
        File.WriteAllText(Path.Combine(src, "keep.txt"), "keep");
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(src, outputDir, null, null, out string? isoPath, null, null,
            excludePatterns: ["**/*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        string extractDir = CreateTempDir();
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
        string src = CreateSourceTree();
        string outputDir = CreateTempDir();

        (int result, string? isoPath) = await XisoWriter.CreateXisoAsync(
            src, outputDir, null, null, null, null, excludePatterns: ["**/node_modules/**", "*.tmp"]);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        HashSet<string> files = ExtractFileSet(isoPath);
        Assert.Contains("keep.txt", files);
        Assert.Contains("sub/data.bin", files);
        Assert.DoesNotContain("skip.tmp", files);
        Assert.DoesNotContain("node_modules/pkg/index.js", files);
    }
}
