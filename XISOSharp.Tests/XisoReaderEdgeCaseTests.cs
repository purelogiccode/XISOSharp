using System.Security.Cryptography;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Additional integration tests for edge cases in <see cref="XisoReader"/>:
/// ListDirectory edge cases, ComputeFileHash edge cases, and cancellation behavior.
/// </summary>
[Collection("Sequential")]
public class XisoReaderEdgeCaseTests : IDisposable
{
    // Resolved via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal.
    private static readonly string TestDataRoot = TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory);

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_edge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTestIso()
    {
        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    #region ListDirectory edge cases

    [Fact]
    public void ListDirectory_NonExistentPath_ThrowsInvalidDataException()
    {
        string isoPath = CreateTestIso();
        Assert.Throws<InvalidDataException>(() => XisoReader.ListDirectory(isoPath, "/nonexistent"));
    }

    [Fact]
    public void ListDirectory_DeeplyNestedPath_WorksCorrectly()
    {
        string isoPath = CreateTestIso();
        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath, "/subdir/nested");
        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "deep.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ListDirectory_InvalidIso_ThrowsXisoFormatException()
    {
        string tempFile = Path.Combine(CreateTempDir(), "bad.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(tempFile));
    }

    [Fact]
    public void ListDirectory_EntryInfo_SectorAndSizeAreConsistent()
    {
        string isoPath = CreateTestIso();
        IReadOnlyList<EntryInfo> entries = XisoReader.ListDirectory(isoPath);

        foreach (EntryInfo entry in entries)
        {
            if (!entry.IsDirectory)
            {
                Assert.True(entry.StartSector > 0, $"File {entry.Name} should have non-zero start sector");
                Assert.True(entry.FileSize > 0, $"File {entry.Name} should have non-zero size");
            }
        }
    }

    #endregion

    #region GetEntryInfo edge cases

    [Fact]
    public void GetEntryInfo_DeepPath_ReturnsCorrectEntry()
    {
        string isoPath = CreateTestIso();
        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/subdir/nested/deep.txt");

        Assert.NotNull(entry);
        Assert.Equal("deep.txt", entry.Name);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void GetEntryInfo_InvalidIso_ThrowsXisoFormatException()
    {
        string tempFile = Path.Combine(CreateTempDir(), "bad.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        Assert.Throws<XisoFormatException>(() => XisoReader.GetEntryInfo(tempFile, "/file.txt"));
    }

    [Fact]
    public void GetEntryInfo_EmptyPath_ReturnsNull()
    {
        string isoPath = CreateTestIso();
        Assert.Null(XisoReader.GetEntryInfo(isoPath, ""));
    }

    [Fact]
    public void GetEntryInfo_PathWithTrailingSlash_ReturnsDirectoryEntry()
    {
        string isoPath = CreateTestIso();
        // A trailing slash is ignored: "/subdir/" resolves to the "subdir" directory entry.
        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, "/subdir/");
        Assert.NotNull(entry);
        Assert.Equal("subdir", entry.Name);
        Assert.True(entry.IsDirectory);
    }

    #endregion

    #region ComputeFileHash edge cases

    [Fact]
    public void ComputeFileHash_AllSupportedAlgorithms_ReturnHash()
    {
        string isoPath = CreateTestIso();

        byte[]? md5 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);
        byte[]? sha1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA1);
        byte[]? sha256 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        byte[]? sha384 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA384);
        byte[]? sha512 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA512);

        Assert.NotNull(md5);
        Assert.NotNull(sha1);
        Assert.NotNull(sha256);
        Assert.NotNull(sha384);
        Assert.NotNull(sha512);

        Assert.Equal(16, md5.Length);
        Assert.Equal(20, sha1.Length);
        Assert.Equal(32, sha256.Length);
        Assert.Equal(48, sha384.Length);
        Assert.Equal(64, sha512.Length);
    }

    [Fact]
    public void ComputeFileHash_SHA1_ReturnsCorrectLength()
    {
        string isoPath = CreateTestIso();
        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA1);

        Assert.NotNull(hash);
        Assert.Equal(20, hash.Length); // SHA-1 is 20 bytes
    }

    [Fact]
    public void ComputeFileHash_SHA512_ReturnsCorrectLength()
    {
        string isoPath = CreateTestIso();
        byte[]? hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA512);

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA-512 is 64 bytes
    }

    [Fact]
    public void ComputeFileHash_MD5_IsDeterministic()
    {
        string isoPath = CreateTestIso();

        byte[]? hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);
        byte[]? hash2 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeDirectoryHashes_EmptyDirectory_ReturnsEmpty()
    {
        string srcDir = CreateTempDir();
        string outputDir = CreateTempDir();

        // Create source with only an empty subdirectory
        Directory.CreateDirectory(Path.Combine(srcDir, "empty"));

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        IReadOnlyList<(string Path, byte[] Hash)> hashes =
            XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256);

        // Should have no file hashes (only empty dir exists)
        Assert.Empty(hashes);
    }

    [Fact]
    public void ComputeDirectoryHashes_NestedDirectory_ReturnsAllFiles()
    {
        string isoPath = CreateTestIso();

        IReadOnlyList<(string Path, byte[] Hash)> hashes =
            XisoReader.ComputeDirectoryHashes(isoPath, "/subdir", HashAlgorithmName.SHA256);

        Assert.NotEmpty(hashes);
        // Should include subfile.txt and nested/deep.txt
        Assert.Contains(hashes, static h => h.Path.Contains("subfile.txt", StringComparison.Ordinal));
        Assert.Contains(hashes, static h => h.Path.Contains("deep.txt", StringComparison.Ordinal));
    }

    #endregion

    #region GetVolumeInfo edge cases

    [Fact]
    public void GetVolumeInfo_VerySmallFile_ReturnsNotValid()
    {
        string tempFile = Path.Combine(CreateTempDir(), "tiny.bin");
        File.WriteAllBytes(tempFile, new byte[10]);

        VolumeInfo info = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(info.IsValid);
    }

    [Fact]
    public void GetVolumeInfo_ExactHeaderSize_ReturnsNotValid()
    {
        string tempFile = Path.Combine(CreateTempDir(), "header_only.bin");
        File.WriteAllBytes(tempFile, new byte[Constants.HeaderOffset + Constants.HeaderDataLength]);

        VolumeInfo info = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(info.IsValid);
    }

    [Fact]
    public void GetVolumeInfo_CreatedIso_HasCorrectMetadata()
    {
        string isoPath = CreateTestIso();
        VolumeInfo info = XisoReader.GetVolumeInfo(isoPath);

        Assert.True(info.IsValid);
        Assert.True(info.RootDirSector > 0);
        Assert.True(info.RootDirSize > 0);
        Assert.True(info.FileLength > 0);
        Assert.True(info.TotalSectors > 0);
        Assert.Equal(0, info.DiscLseek);
    }

    [Fact]
    public void GetVolumeInfo_FileLength_MatchesActualFileSize()
    {
        string isoPath = CreateTestIso();
        VolumeInfo info = XisoReader.GetVolumeInfo(isoPath);
        long actualSize = new FileInfo(isoPath).Length;

        Assert.Equal(actualSize, info.FileLength);
    }

    [Fact]
    public void GetVolumeInfo_TotalSectors_MatchesFileLength()
    {
        string isoPath = CreateTestIso();
        VolumeInfo info = XisoReader.GetVolumeInfo(isoPath);

        long expectedSectors = info.FileLength / Constants.SectorSize;
        Assert.Equal(expectedSectors, info.TotalSectors);
    }

    #endregion

    #region CopyOut edge cases

    [Fact]
    public void CopyOut_DirectoryWithNestedFiles_ExtractsAll()
    {
        string isoPath = CreateTestIso();
        string destDir = CreateTempDir();
        string destPath = Path.Combine(destDir, "subdir_copy");

        XisoReader.CopyOut(isoPath, "/subdir", destPath);

        Assert.True(Directory.Exists(destPath));
        Assert.True(File.Exists(Path.Combine(destPath, "subfile.txt")));
        Assert.True(File.Exists(Path.Combine(destPath, "nested", "deep.txt")));
    }

    [Fact]
    public void CopyOut_EmptyFile_CreatesZeroLengthFile()
    {
        string srcDir = CreateTempDir();
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "empty.txt"), "");
        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "empty.txt");
        XisoReader.CopyOut(isoPath, "/empty.txt", destPath);

        Assert.True(File.Exists(destPath));
        Assert.Equal(0, new FileInfo(destPath).Length);
    }

    [Fact]
    public void CopyOut_LargeFile_PreservesContent()
    {
        string srcDir = CreateTempDir();
        string outputDir = CreateTempDir();
        string destDir = CreateTempDir();

        byte[] data = new byte[512 * 1024]; // 512KB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(srcDir, "large.bin"), data);

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        string destPath = Path.Combine(destDir, "large.bin");
        XisoReader.CopyOut(isoPath, "/large.bin", destPath);

        byte[] extracted = File.ReadAllBytes(destPath);
        Assert.Equal(data, extracted);
    }

    #endregion

    #region Cancellation

    [Fact]
    public void DecodeXiso_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        string isoPath = CreateTestIso();
        string extractDir = CreateTempDir();
        CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoReader.DecodeXiso(isoPath, extractDir, ExtractMode.Extract, out _, false, cts.Token));
    }

    [Fact]
    public async Task DecodeXisoAsync_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        string isoPath = CreateTestIso();
        string extractDir = CreateTempDir();
        CancellationTokenSource cts = new();
        await cts.CancelAsync();

#pragma warning disable MA0004
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await XisoReader.DecodeXisoAsync(isoPath, extractDir, ExtractMode.Extract, false, cts.Token));
#pragma warning restore MA0004
    }

    #endregion

    #region VerifyXiso XGD offset detection

    [Fact]
    public void VerifyXiso_RawIso_DetectsZeroOffset()
    {
        string isoPath = CreateTestIso();

        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (_, _, long discLseek) = XisoReader.VerifyXiso(fs, "test.iso");

        Assert.Equal(0, discLseek);
    }

    #endregion

    #region Rewrite edge cases

    [Fact]
    public void Rewrite_ThenExtract_PreservesAllContent()
    {
        string createDir = CreateTempDir();
        string rewriteDir = CreateTempDir();
        string extractDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath);
        Assert.NotNull(rewrittenPath);

        XisoReader.Extract(rewrittenPath, extractDir, false);

        // Verify all files preserved
        Assert.True(File.Exists(Path.Combine(extractDir, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "file2.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "binary.bin")));
        Assert.True(File.Exists(Path.Combine(extractDir, "test.xbe")));
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "subfile.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "subdir", "nested", "deep.txt")));
    }

    [Fact]
    public void Rewrite_WithCustomName_UsesProvidedName()
    {
        string createDir = CreateTempDir();
        string rewriteDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath, outputName: "rewritten_custom");

        Assert.NotNull(rewrittenPath);
        // The outputName controls the ISO filename within the output directory
        Assert.True(File.Exists(rewrittenPath), $"Rewritten ISO not found at {rewrittenPath}");
    }

    #endregion
}
