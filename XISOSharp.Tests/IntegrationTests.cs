using System.Security.Cryptography;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Integration tests that exercise the full XISO create, extract, list, and rewrite
/// pipeline against real test data on disk.
/// </summary>
[Collection("Sequential")]
public class IntegrationTests : IDisposable
{
    // Resolved via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal.
    private static readonly string TestDataRoot = TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory);

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void CreateXiso_TrailingSeparator_NamesIsoLikeBareDirectory()
    {
        // BUG-LIB-024: trailing separators strip before leaf parsing, so
        // "source/" and "source" produce the same output name. The alt
        // separator only counts where the OS treats it as one (Windows).
        List<char> seps = new() { Path.DirectorySeparatorChar };
        if (OperatingSystem.IsWindows() && Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            seps.Add(Path.AltDirectorySeparatorChar);

        foreach (char sep in seps)
        {
            string outputDir = CreateTempDir();
            int result = XisoWriter.CreateXiso(SourceDir + sep, outputDir, null, null, out string? outPath, null, null);

            Assert.Equal(0, result);
            Assert.NotNull(outPath);
            Assert.Equal("source.iso", Path.GetFileName(outPath));
        }
    }

    [Fact]
    public void CreateXiso_FromDirectory_ProducesValidIso()
    {
        string outputDir = CreateTempDir();

        int result = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? outPath, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath), $"Output ISO not found at {outPath}");
        Assert.True(new FileInfo(outPath).Length > 0, "Output ISO is empty");
    }

    [Fact]
    public void CreateXiso_ThenExtract_RoundTripsSuccessfully()
    {
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();

        // Create ISO
        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // Extract ISO
        int extractResult = XisoReader.Extract(isoPath, extractDir, false);
        Assert.Equal(0, extractResult);

        // Verify extracted files exist
        Assert.True(File.Exists(Path.Combine(extractDir, "file1.txt")), "file1.txt missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "file2.txt")), "file2.txt missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "binary.bin")), "binary.bin missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "test.xbe")), "test.xbe missing after extract");
        Assert.True(Directory.Exists(Path.Combine(extractDir, "subdir")), "subdir missing after extract");
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "subfile.txt")),
            "subfile.txt missing after extract");
    }

    [Fact]
    public void CreateXiso_ThenExtract_PreservesFileContent()
    {
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        int extractResult = XisoReader.Extract(isoPath, extractDir, false);
        Assert.Equal(0, extractResult);

        // Compare file1.txt content
        string originalContent = File.ReadAllText(Path.Combine(SourceDir, "file1.txt"));
        string extractedContent = File.ReadAllText(Path.Combine(extractDir, "file1.txt"));
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void ListXiso_ReturnsFilesWithoutExtracting()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // List should succeed without extracting
        int listResult = XisoReader.List(isoPath, false);
        Assert.Equal(0, listResult);
    }

    [Fact]
    public void VerifyXiso_ValidatesCreatedIso()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (uint rootDirSector, uint rootDirSize, _) = XisoReader.VerifyXiso(fs, "test.iso");

        Assert.True(rootDirSector > 0, "Root directory sector should be non-zero");
        Assert.True(rootDirSize > 0, "Root directory size should be non-zero");
    }

    [Fact]
    public void DecodeXiso_Rewrite_ProducesOptimizedIso()
    {
        string createDir = CreateTempDir();
        string rewriteDir = CreateTempDir();

        // Create an ISO first
        int createResult = XisoWriter.CreateXiso(SourceDir, createDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        // Rewrite (optimize) it into a different directory
        int rewriteResult = XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath);
        Assert.Equal(0, rewriteResult);
        Assert.NotNull(rewrittenPath);
        Assert.True(File.Exists(rewrittenPath), "Rewritten ISO not found");
    }

    [Fact]
    public void CreateXiso_WithProgressCallback_ReportsProgress()
    {
        string outputDir = CreateTempDir();
        bool progressCalled = false;
        long lastCurrent = 0;

        int result = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out _, null,
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
        string outputDir = CreateTempDir();
        CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        Assert.Throws<OperationCanceledException>(() =>
            XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out _, null, null, cts.Token));
    }

    [Fact]
    public async Task CreateXisoAsync_RunsAsynchronously()
    {
        string outputDir = CreateTempDir();

        (int result, string? outPath) = await XisoWriter.CreateXisoAsync(
            SourceDir, outputDir, null, null, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task DecodeXisoAsync_Extract_RunsAsynchronously()
    {
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();

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
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        int extractResult = XisoReader.Extract(isoPath, extractDir, false);
        Assert.Equal(0, extractResult);

        // Check nested structure: subdir/nested/deep.txt
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "nested", "deep.txt")),
            "Deeply nested file missing after extract");

        string content = File.ReadAllText(Path.Combine(extractDir, "subdir", "nested", "deep.txt"));
        Assert.False(string.IsNullOrEmpty(content), "Deeply nested file should have content");
    }

    [Fact]
    public void ExtractXiso_ToCustomOutputDirectory_Works()
    {
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();
        string customSubdir = Path.Combine(extractDir, "my_custom_dir");

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        int extractResult = XisoReader.Extract(isoPath, customSubdir, false);
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
        string srcDir = CreateTempDir();
        string outputDir = CreateTempDir();
        string extractDir = CreateTempDir();

        // Create a file whose name contains é (U+00E9, byte 0xE9 in Latin-1)
        string nonAsciiName = "café" + (char)0xE9 + ".txt";
        string filePath = Path.Combine(srcDir, nonAsciiName);
        File.WriteAllText(filePath, "non-ascii content");

        int createResult = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        int extractResult = XisoReader.Extract(isoPath, extractDir, false);
        Assert.Equal(0, extractResult);

        string[] extractedFiles = Directory.GetFiles(extractDir);
        Assert.Single(extractedFiles);
        string extractedName = Path.GetFileName(extractedFiles[0]);
        Assert.Equal(nonAsciiName, extractedName);
        Assert.Equal("non-ascii content", File.ReadAllText(extractedFiles[0]));
    }

    [Fact]
    public void Tree_ListsAllFilesWithSizes()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        StringWriter sw = new();
        TextWriter origOut = Logger.Out;
        Logger.Out = sw;
        try
        {
            int treeResult = XisoReader.Tree(isoPath, false);
            Assert.Equal(0, treeResult);
        }
        finally
        {
            Logger.Out = origOut;
        }

        string output = sw.ToString();

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
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        VolumeInfo volInfo = XisoReader.GetVolumeInfo(isoPath);

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
        string tempFile = Path.Combine(CreateTempDir(), "not_an_iso.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        VolumeInfo volInfo = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(volInfo.IsValid);
    }

    [Fact]
    public void ListDirectory_Root_ReturnsEntries()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "file1.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "file2.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "binary.bin", StringComparison.Ordinal));
        Assert.Contains(entries, static e => string.Equals(e.Name, "test.xbe", StringComparison.Ordinal));
        Assert.Contains(entries,
            static e => string.Equals(e.Name, "subdir", StringComparison.Ordinal) && e.IsDirectory);
    }

    [Fact]
    public void ListDirectory_Subdirectory_ReturnsEntries()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath, "/subdir");

        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "subfile.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void GetEntryInfo_ExistingFile_ReturnsInfo()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/file1.txt");

        Assert.NotNull(entry);
        Assert.Equal("file1.txt", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.True(entry.FileSize > 0);
    }

    [Fact]
    public void GetEntryInfo_ExistingDirectory_ReturnsInfo()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/subdir");

        Assert.NotNull(entry);
        Assert.Equal("subdir", entry.Name);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void GetEntryInfo_NonExistentFile_ReturnsNull()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/nonexistent.txt");

        Assert.Null(entry);
    }

    [Fact]
    public void GetEntryInfo_RootPath_ReturnsNull()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/");

        Assert.Null(entry);
    }

    [Fact]
    public void ListDirectory_EntryInfo_HasCorrectAttributes()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath);

        EntryInfo subdir = entries.First(static e => string.Equals(e.Name, "subdir", StringComparison.Ordinal));
        Assert.True(subdir.IsDirectory);
        Assert.NotEqual(0, subdir.Attributes & Constants.AttributeDir);

        EntryInfo file = entries.First(static e => string.Equals(e.Name, "file1.txt", StringComparison.Ordinal));
        Assert.False(file.IsDirectory);
        Assert.NotEqual(0, file.Attributes & Constants.AttributeArc);
    }

    [Fact]
    public void ComputeFileHash_MD5_ReturnsHash()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);

        Assert.NotNull(hash);
        Assert.Equal(16, hash.Length); // MD5 is 16 bytes
    }

    [Fact]
    public void ComputeFileHash_SHA256_ReturnsHash()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length); // SHA-256 is 32 bytes
    }

    [Fact]
    public void ComputeFileHash_SameFile_SameHash()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        byte[]? hash2 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeFileHash_ParallelCalls_Agree()
    {
        // BUG-LIB-010: the copy scratch buffer is rented per call, so
        // concurrent hashes (e.g. under Task.Run) must never share state.
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? expected = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        Assert.NotNull(expected);

        byte[][] results = new byte[8][];
        Parallel.For(0, results.Length, i =>
            results[i] = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256)!);

        foreach (byte[] actual in results)
        {
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ComputeFileHash_DifferentFiles_DifferentHash()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        byte[]? hash2 = XisoReader.ComputeFileHash(isoPath, "/file2.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeFileHash_NonExistentFile_ReturnsNull()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/nonexistent.txt", HashAlgorithmName.SHA256);

        Assert.Null(hash);
    }

    [Fact]
    public void ComputeFileHash_Directory_ThrowsInvalidDataException()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ComputeFileHash(isoPath, "/subdir", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void ComputeDirectoryHashes_Root_ReturnsAllFiles()
    {
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        IReadOnlyList<(string Path, byte[] Hash)> results = XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256);

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
        string outputDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        IReadOnlyList<(string Path, byte[] Hash)> results = XisoReader.ComputeDirectoryHashes(isoPath, "/subdir", HashAlgorithmName.MD5);

        Assert.NotEmpty(results);
        Assert.Contains(results, static r => string.Equals(r.Path, "/subdir/subfile.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeFileHash_EmptyFile_ReturnsEmptyHash()
    {
        string srcDir = CreateTempDir();
        string outputDir = CreateTempDir();

        // Create an empty file
        File.WriteAllText(Path.Combine(srcDir, "empty.txt"), "");

        int createResult = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/empty.txt", HashAlgorithmName.SHA256);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
        // SHA-256 of empty input is a known value
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Fact]
    public void CopyOut_SingleFile_ExtractsToDestination()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "extracted_file.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        Assert.True(File.Exists(destPath));
        string content = File.ReadAllText(destPath);
        Assert.False(string.IsNullOrEmpty(content));
    }

    [Fact]
    public void CopyOut_SingleFile_PreservesContent()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string originalContent = File.ReadAllText(Path.Combine(SourceDir, "file1.txt"));

        string destPath = Path.Combine(destDir, "file1.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        string extractedContent = File.ReadAllText(destPath);
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void CopyOut_Directory_ExtractsAllContents()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "extracted_subdir");
        XisoReader.CopyOut(isoPath, "/subdir", destPath);

        Assert.True(Directory.Exists(destPath));
        Assert.True(File.Exists(Path.Combine(destPath, "subfile.txt")));
    }

    [Fact]
    public void CopyOut_NonExistentPath_ThrowsInvalidDataException()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "output.txt");
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.CopyOut(isoPath, "/nonexistent.txt", destPath));
    }

    [Fact]
    public void CopyOut_NestedFile_ExtractsCorrectly()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "deep.txt");
        XisoReader.CopyOut(isoPath, "/subdir/nested/deep.txt", destPath);

        Assert.True(File.Exists(destPath));
        string originalContent = File.ReadAllText(Path.Combine(SourceDir, "subdir", "nested", "deep.txt"));
        string extractedContent = File.ReadAllText(destPath);
        Assert.Equal(originalContent, extractedContent);
    }

    [Fact]
    public void CopyOut_CreatesDestinationDirectory()
    {
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        int createResult = XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, createResult);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "new_subdir", "file1.txt");
        XisoReader.CopyOut(isoPath, "/file1.txt", destPath);

        Assert.True(File.Exists(destPath));
    }
}
