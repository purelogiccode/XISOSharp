namespace XISOSharp.Tests;

/// <summary>
/// Integration tests that exercise the full XISO create, extract, list, and rewrite
/// pipeline against real test data on disk.
/// </summary>
[Collection("Sequential")]
public class IntegrationTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");
    private static readonly string OutputIso = Path.Combine(TestDataRoot, "output", "source.iso");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best effort cleanup */ }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void CreateXiso_FromDirectory_ProducesValidIso()
    {
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var outPath, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath), $"Output ISO not found at {outPath}");
        Assert.True(new FileInfo(outPath).Length > 0, "Output ISO is empty");
    }

    [Fact]
    public void CreateXiso_ThenExtract_RoundTripsSuccessfully()
    {
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create ISO
        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // Extract ISO
        var extractResult = XisoReader.Extract(isoPath, extractDir, llCompat: false);
        Assert.Equal(0, extractResult);

        // Verify extracted files exist
        Assert.True(File.Exists(Path.Combine(extractDir, "file1.txt")), "file1.txt missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "file2.txt")), "file2.txt missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "binary.bin")), "binary.bin missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "test.xbe")), "test.xbe missing after extract");
        Assert.True(Directory.Exists(Path.Combine(extractDir, "subdir")), "subdir missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "subfile.txt")), "subfile.txt missing after extract");
    }

    [Fact]
    public void CreateXiso_ThenExtract_PreservesFileContent()
    {
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var extractResult = XisoReader.Extract(isoPath, extractDir, llCompat: false);
        Assert.Equal(0, extractResult);

        // Compare file1.txt content
        var originalContent = File.ReadAllText(Path.Combine(SourceDir, "file1.txt"));
        var extractedContent = File.ReadAllText(Path.Combine(extractDir, "file1.txt"));
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void ListXiso_ReturnsFilesWithoutExtracting()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // List should succeed without extracting
        var listResult = XisoReader.List(isoPath, llCompat: false);
        Assert.Equal(0, listResult);
    }

    [Fact]
    public void VerifyXiso_ValidatesCreatedIso()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (uint rootDirSector, uint rootDirSize, _) = XisoReader.VerifyXiso(fs, "test.iso");

        Assert.True(rootDirSector > 0, "Root directory sector should be non-zero");
        Assert.True(rootDirSize > 0, "Root directory size should be non-zero");
    }

    [Fact]
    public void DecodeXiso_Rewrite_ProducesOptimizedIso()
    {
        var createDir = CreateTempDir();
        var rewriteDir = CreateTempDir();

        // Create an ISO first
        var createResult = XisoWriter.CreateXiso(SourceDir, createDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // Rewrite (optimize) it into a different directory
        var rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath), "Rewritten ISO not found");
    }

    [Fact]
    public void CreateXiso_WithProgressCallback_ReportsProgress()
    {
        var outputDir = CreateTempDir();
        var progressCalled = false;
        long lastCurrent = 0;

        var result = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out _, null,
            (current, _) =>
            {
                progressCalled = true;
                lastCurrent = current;
            });

        Assert.Equal(0, result);
        Assert.True(progressCalled, "Progress callback should have been called");
        Assert.True(lastCurrent > 0, "Progress should have reported bytes written");
    }

    [Fact]
    public void CreateXiso_WithCancellationToken_ThrowsOnCancellation()
    {
        var outputDir = CreateTempDir();
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        Assert.Throws<OperationCanceledException>(() =>
            XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out _, null, null, cts.Token));
    }

    [Fact]
    public async Task CreateXisoAsync_RunsAsynchronously()
    {
        var outputDir = CreateTempDir();

        (int result, string? outPath) = await XisoWriter.CreateXisoAsync(
            SourceDir, outputDir, null, null, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task DecodeXisoAsync_Extract_RunsAsynchronously()
    {
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        (int createResult, string? isoPath) = await XisoWriter.CreateXisoAsync(
            SourceDir, outputDir, null, null, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        (int extractResult, _) = await XisoReader.DecodeXisoAsync(
            isoPath, extractDir, ExtractMode.Extract);
        Assert.Equal(0, extractResult);
        Assert.True(File.Exists(Path.Combine(extractDir, "file1.txt")));
    }

    [Fact]
    public void CreateXiso_NestedSubdirectories_PreservesStructure()
    {
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var extractResult = XisoReader.Extract(isoPath, extractDir, llCompat: false);
        Assert.Equal(0, extractResult);

        // Check nested structure: subdir/nested/deep.txt
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "nested", "deep.txt")),
            "Deeply nested file missing after extract");

        var content = File.ReadAllText(Path.Combine(extractDir, "subdir", "nested", "deep.txt"));
        Assert.False(string.IsNullOrEmpty(content), "Deeply nested file should have content");
    }

    [Fact]
    public void ExtractXiso_ToCustomOutputDirectory_Works()
    {
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();
        var customSubdir = Path.Combine(extractDir, "my_custom_dir");

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var extractResult = XisoReader.Extract(isoPath, customSubdir, llCompat: false);
        Assert.Equal(0, extractResult);
        Assert.True(File.Exists(Path.Combine(customSubdir, "file1.txt")));
    }

    /// <summary>
    /// Verifies that filenames containing non-ASCII Latin-1 bytes (e.g. 0xE9 = é)
    /// survive a full create → extract round-trip without being replaced by '?'.
    /// </summary>
    [Fact]
    public void CreateXiso_ThenExtract_NonAsciiFilename_PreservesLatin1Bytes()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create a file whose name contains é (U+00E9, byte 0xE9 in Latin-1)
        var nonAsciiName = "café" + (char)0xE9 + ".txt";
        var filePath = Path.Combine(srcDir, nonAsciiName);
        File.WriteAllText(filePath, "non-ascii content");

        var createResult = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var extractResult = XisoReader.Extract(isoPath, extractDir, llCompat: false);
        Assert.Equal(0, extractResult);

        var extractedFiles = Directory.GetFiles(extractDir);
        Assert.Single(extractedFiles);
        var extractedName = Path.GetFileName(extractedFiles[0]);
        Assert.Equal(nonAsciiName, extractedName);
        Assert.Equal("non-ascii content", File.ReadAllText(extractedFiles[0]));
    }
}
