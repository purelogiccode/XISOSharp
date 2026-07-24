using System.Security.Cryptography;

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
        var extractResult = XisoReader.Extract(isoPath, extractDir, false);
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

        var extractResult = XisoReader.Extract(isoPath, extractDir, false);
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
        var listResult = XisoReader.List(isoPath, false);
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

        var extractResult = XisoReader.Extract(isoPath, extractDir, false);
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

        var extractResult = XisoReader.Extract(isoPath, customSubdir, false);
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

        var extractResult = XisoReader.Extract(isoPath, extractDir, false);
        Assert.Equal(0, extractResult);

        var extractedFiles = Directory.GetFiles(extractDir);
        Assert.Single(extractedFiles);
        var extractedName = Path.GetFileName(extractedFiles[0]);
        Assert.Equal(nonAsciiName, extractedName);
        Assert.Equal("non-ascii content", File.ReadAllText(extractedFiles[0]));
    }

    [Fact]
    public void Tree_ListsAllFilesWithSizes()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var sw = new StringWriter();
        var origOut = Logger.Out;
        Logger.Out = sw;
        try
        {
            var treeResult = XisoReader.Tree(isoPath, false);
            Assert.Equal(0, treeResult);
        }
        finally
        {
            Logger.Out = origOut;
        }

        var output = sw.ToString();

        Assert.Contains("file1.txt", output, StringComparison.Ordinal);
        Assert.Contains("file2.txt", output, StringComparison.Ordinal);
        Assert.Contains("binary.bin", output, StringComparison.Ordinal);
        Assert.Contains("test.xbe", output, StringComparison.Ordinal);
        Assert.Contains("subdir", output, StringComparison.Ordinal);
        Assert.Contains("subfile.txt", output, StringComparison.Ordinal);
        Assert.Contains("bytes", output, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVolumeInfo_ValidIso_ReturnsValidInfo()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var volInfo = XisoReader.GetVolumeInfo(isoPath);

        Assert.True(volInfo.IsValid);
        Assert.True(volInfo.RootDirSector > 0);
        Assert.True(volInfo.RootDirSize > 0);
        Assert.True(volInfo.FileLength > 0);
        Assert.True(volInfo.TotalSectors > 0);
        Assert.Equal(0, volInfo.DiscLseek);
    }

    [Fact]
    public void GetVolumeInfo_InvalidFile_ReturnsNotValid()
    {
        var tempFile = Path.Combine(CreateTempDir(), "not_an_iso.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        var volInfo = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(volInfo.IsValid);
    }

    [Fact]
    public void ListDirectory_Root_ReturnsEntries()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entries = XisoReader.ListDirectory(isoPath, "/");

        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "file1.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "file2.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "binary.bin", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "test.xbe", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "subdir", StringComparison.Ordinal) && e.IsDirectory);
    }

    [Fact]
    public void ListDirectory_Subdirectory_ReturnsEntries()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entries = XisoReader.ListDirectory(isoPath, "/subdir");

        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "subfile.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void GetEntryInfo_ExistingFile_ReturnsInfo()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entry = XisoReader.GetEntryInfo(isoPath, "/file1.txt");

        Assert.NotNull(entry);
        Assert.Equal("file1.txt", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.True(entry.FileSize > 0);
    }

    [Fact]
    public void GetEntryInfo_ExistingDirectory_ReturnsInfo()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entry = XisoReader.GetEntryInfo(isoPath, "/subdir");

        Assert.NotNull(entry);
        Assert.Equal("subdir", entry.Name);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void GetEntryInfo_NonExistentFile_ReturnsNull()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entry = XisoReader.GetEntryInfo(isoPath, "/nonexistent.txt");

        Assert.Null(entry);
    }

    [Fact]
    public void GetEntryInfo_RootPath_ReturnsNull()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entry = XisoReader.GetEntryInfo(isoPath, "/");

        Assert.Null(entry);
    }

    [Fact]
    public void ListDirectory_EntryInfo_HasCorrectAttributes()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var entries = XisoReader.ListDirectory(isoPath, "/");

        var subdir = entries.First(static e => string.Equals(e.Name, "subdir", StringComparison.Ordinal));
        Assert.True(subdir.IsDirectory);
        Assert.True((subdir.Attributes & Constants.AttributeDir) != 0);

        var file = entries.First(static e => string.Equals(e.Name, "file1.txt", StringComparison.Ordinal));
        Assert.False(file.IsDirectory);
        Assert.True((file.Attributes & Constants.AttributeArc) != 0);
    }

    [Fact]
    public void ComputeFileHash_MD5_ReturnsHash()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);

        Assert.NotNull(hash);
        Assert.Equal(16, hash.Length); // MD5 is 16 bytes
    }

    [Fact]
    public void ComputeFileHash_SHA256_ReturnsHash()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length); // SHA-256 is 32 bytes
    }

    [Fact]
    public void ComputeFileHash_SameFile_SameHash()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        var hash2 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeFileHash_DifferentFiles_DifferentHash()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        var hash2 = XisoReader.ComputeFileHash(isoPath, "/file2.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeFileHash_NonExistentFile_ReturnsNull()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash = XisoReader.ComputeFileHash(isoPath, "/nonexistent.txt", HashAlgorithmName.SHA256);

        Assert.Null(hash);
    }

    [Fact]
    public void ComputeFileHash_Directory_ThrowsInvalidDataException()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ComputeFileHash(isoPath, "/subdir", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void ComputeDirectoryHashes_Root_ReturnsAllFiles()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var results = XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256);

        Assert.NotEmpty(results);
        Assert.Contains(results, static r => string.Equals(r.Path, "/file1.txt", StringComparison.Ordinal));
        Assert.Contains(results, static r => string.Equals(r.Path, "/file2.txt", StringComparison.Ordinal));
        Assert.Contains(results, static r => string.Equals(r.Path, "/binary.bin", StringComparison.Ordinal));
        Assert.Contains(results, static r => string.Equals(r.Path, "/test.xbe", StringComparison.Ordinal));
        Assert.Contains(results, static r => string.Equals(r.Path, "/subdir/subfile.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeDirectoryHashes_Subdirectory_ReturnsFilesInDir()
    {
        var outputDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var results = XisoReader.ComputeDirectoryHashes(isoPath, "/subdir", HashAlgorithmName.MD5);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => string.Equals(r.Path, "/subdir/subfile.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeFileHash_EmptyFile_ReturnsEmptyHash()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        // Create an empty file
        File.WriteAllText(Path.Combine(srcDir, "empty.txt"), "");

        var createResult = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var hash = XisoReader.ComputeFileHash(isoPath, "/empty.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
        // SHA-256 of empty input is a known value
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Fact]
    public void CopyOut_SingleFile_ExtractsToDestination()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "extracted_file.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        Assert.True(File.Exists(destPath));
        var content = File.ReadAllText(destPath);
        Assert.False(string.IsNullOrEmpty(content));
    }

    [Fact]
    public void CopyOut_SingleFile_PreservesContent()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var originalContent = File.ReadAllText(Path.Combine(SourceDir, "file1.txt"));

        var destPath = Path.Combine(destDir, "file1.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        var extractedContent = File.ReadAllText(destPath);
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void CopyOut_Directory_ExtractsAllContents()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "extracted_subdir");
        XisoReader.CopyOut(isoPath, "/subdir", destPath);

        Assert.True(Directory.Exists(destPath));
        Assert.True(File.Exists(Path.Combine(destPath, "subfile.txt")));
    }

    [Fact]
    public void CopyOut_NonExistentPath_ThrowsInvalidDataException()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "output.txt");
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.CopyOut(isoPath, "/nonexistent.txt", destPath));
    }

    [Fact]
    public void CopyOut_NestedFile_ExtractsCorrectly()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "deep.txt");
        XisoReader.CopyOut(isoPath, "/subdir/nested/deep.txt", destPath);

        Assert.True(File.Exists(destPath));
        var originalContent = File.ReadAllText(Path.Combine(SourceDir, "subdir", "nested", "deep.txt"));
        var extractedContent = File.ReadAllText(destPath);
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void CopyOut_CreatesDestinationDirectory()
    {
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "new_subdir", "file1.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        Assert.True(File.Exists(destPath));
    }
}
