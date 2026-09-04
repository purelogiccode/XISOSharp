using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Additional integration tests for edge cases in <see cref="XisoReader"/>:
/// ListDirectory edge cases, ComputeFileHash edge cases, and cancellation behavior.
/// </summary>
[Collection("Sequential")]
public class XisoReaderEdgeCaseTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_edge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTestIso()
    {
        var outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    #region ListDirectory edge cases

    [Fact]
    public void ListDirectory_NonExistentPath_ThrowsInvalidDataException()
    {
        var isoPath = CreateTestIso();
        Assert.Throws<InvalidDataException>(() => XisoReader.ListDirectory(isoPath, "/nonexistent"));
    }

    [Fact]
    public void ListDirectory_DeeplyNestedPath_WorksCorrectly()
    {
        var isoPath = CreateTestIso();
        var entries = XisoReader.ListDirectory(isoPath, "/subdir/nested");
        Assert.NotEmpty(entries);
        Assert.Contains(entries, static e => string.Equals(e.Name, "deep.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ListDirectory_InvalidIso_ThrowsXisoFormatException()
    {
        var tempFile = Path.Combine(CreateTempDir(), "bad.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        Assert.Throws<XisoFormatException>(() => XisoReader.ListDirectory(tempFile));
    }

    [Fact]
    public void ListDirectory_EntryInfo_SectorAndSizeAreConsistent()
    {
        var isoPath = CreateTestIso();
        var entries = XisoReader.ListDirectory(isoPath);

        foreach (var entry in entries)
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
        var isoPath = CreateTestIso();
        var entry = XisoReader.GetEntryInfo(isoPath, "/subdir/nested/deep.txt");

        Assert.NotNull(entry);
        Assert.Equal("deep.txt", entry.Name);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void GetEntryInfo_InvalidIso_ThrowsXisoFormatException()
    {
        var tempFile = Path.Combine(CreateTempDir(), "bad.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        Assert.Throws<XisoFormatException>(() => XisoReader.GetEntryInfo(tempFile, "/file.txt"));
    }

    [Fact]
    public void GetEntryInfo_EmptyPath_ReturnsNull()
    {
        var isoPath = CreateTestIso();
        Assert.Null(XisoReader.GetEntryInfo(isoPath, ""));
    }

    [Fact]
    public void GetEntryInfo_PathWithTrailingSlash_ReturnsDirectoryEntry()
    {
        var isoPath = CreateTestIso();
        // A trailing slash is ignored: "/subdir/" resolves to the "subdir" directory entry.
        var entry = XisoReader.GetEntryInfo(isoPath, "/subdir/");
        Assert.NotNull(entry);
        Assert.Equal("subdir", entry.Name);
        Assert.True(entry.IsDirectory);
    }

    #endregion

    #region ComputeFileHash edge cases

    [Fact]
    public void ComputeFileHash_AllSupportedAlgorithms_ReturnHash()
    {
        var isoPath = CreateTestIso();

        var md5 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);
        var sha1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA1);
        var sha256 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA256);
        var sha384 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA384);
        var sha512 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA512);

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
        var isoPath = CreateTestIso();
        var hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA1);

        Assert.NotNull(hash);
        Assert.Equal(20, hash.Length); // SHA-1 is 20 bytes
    }

    [Fact]
    public void ComputeFileHash_SHA512_ReturnsCorrectLength()
    {
        var isoPath = CreateTestIso();
        var hash = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.SHA512);

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA-512 is 64 bytes
    }

    [Fact]
    public void ComputeFileHash_MD5_IsDeterministic()
    {
        var isoPath = CreateTestIso();

        var hash1 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);
        var hash2 = XisoReader.ComputeFileHash(isoPath, "/file1.txt", HashAlgorithmName.MD5);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeDirectoryHashes_EmptyDirectory_ReturnsEmpty()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        // Create source with only an empty subdirectory
        Directory.CreateDirectory(Path.Combine(srcDir, "empty"));

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var hashes = XisoReader.ComputeDirectoryHashes(isoPath, "/", HashAlgorithmName.SHA256);

        // Should have no file hashes (only empty dir exists)
        Assert.Empty(hashes);
    }

    [Fact]
    public void ComputeDirectoryHashes_NestedDirectory_ReturnsAllFiles()
    {
        var isoPath = CreateTestIso();

        var hashes = XisoReader.ComputeDirectoryHashes(isoPath, "/subdir", HashAlgorithmName.SHA256);

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
        var tempFile = Path.Combine(CreateTempDir(), "tiny.bin");
        File.WriteAllBytes(tempFile, new byte[10]);

        var info = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(info.IsValid);
    }

    [Fact]
    public void GetVolumeInfo_ExactHeaderSize_ReturnsNotValid()
    {
        var tempFile = Path.Combine(CreateTempDir(), "header_only.bin");
        File.WriteAllBytes(tempFile, new byte[Constants.HeaderOffset + Constants.HeaderDataLength]);

        var info = XisoReader.GetVolumeInfo(tempFile);

        Assert.False(info.IsValid);
    }

    [Fact]
    public void GetVolumeInfo_CreatedIso_HasCorrectMetadata()
    {
        var isoPath = CreateTestIso();
        var info = XisoReader.GetVolumeInfo(isoPath);

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
        var isoPath = CreateTestIso();
        var info = XisoReader.GetVolumeInfo(isoPath);
        var actualSize = new FileInfo(isoPath).Length;

        Assert.Equal(actualSize, info.FileLength);
    }

    [Fact]
    public void GetVolumeInfo_TotalSectors_MatchesFileLength()
    {
        var isoPath = CreateTestIso();
        var info = XisoReader.GetVolumeInfo(isoPath);

        var expectedSectors = info.FileLength / Constants.SectorSize;
        Assert.Equal(expectedSectors, info.TotalSectors);
    }

    #endregion

    #region CopyOut edge cases

    [Fact]
    public void CopyOut_DirectoryWithNestedFiles_ExtractsAll()
    {
        var isoPath = CreateTestIso();
        var destDir = CreateTempDir();
        var destPath = Path.Combine(destDir, "subdir_copy");

        XisoReader.CopyOut(isoPath, "/subdir", destPath);

        Assert.True(Directory.Exists(destPath));
        Assert.True(File.Exists(Path.Combine(destPath, "subfile.txt")));
        Assert.True(File.Exists(Path.Combine(destPath, "nested", "deep.txt")));
    }

    [Fact]
    public void CopyOut_EmptyFile_CreatesZeroLengthFile()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "empty.txt"), "");
        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "empty.txt");
        XisoReader.CopyOut(isoPath, "/empty.txt", destPath);

        Assert.True(File.Exists(destPath));
        Assert.Equal(0, new FileInfo(destPath).Length);
    }

    [Fact]
    public void CopyOut_LargeFile_PreservesContent()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var destDir = CreateTempDir();

        var data = new byte[512 * 1024]; // 512KB
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(srcDir, "large.bin"), data);

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var destPath = Path.Combine(destDir, "large.bin");
        XisoReader.CopyOut(isoPath, "/large.bin", destPath);

        var extracted = File.ReadAllBytes(destPath);
        Assert.Equal(data, extracted);
    }

    #endregion

    #region Cancellation

    [Fact]
    public void DecodeXiso_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        var isoPath = CreateTestIso();
        var extractDir = CreateTempDir();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoReader.DecodeXiso(isoPath, extractDir, ExtractMode.Extract, out _, false, cts.Token));
    }

    [Fact]
    public async Task DecodeXisoAsync_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        var isoPath = CreateTestIso();
        var extractDir = CreateTempDir();
        var cts = new CancellationTokenSource();
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
        var isoPath = CreateTestIso();

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        (_, _, var discLseek) = XisoReader.VerifyXiso(fs, "test.iso");

        Assert.Equal(0, discLseek);
    }

    #endregion

    #region Rewrite edge cases

    [Fact]
    public void Rewrite_ThenExtract_PreservesAllContent()
    {
        var createDir = CreateTempDir();
        var rewriteDir = CreateTempDir();
        var extractDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath);
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
        var createDir = CreateTempDir();
        var rewriteDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath, outputName: "rewritten_custom");

        Assert.NotNull(rewrittenPath);
        // The outputName controls the ISO filename within the output directory
        Assert.True(File.Exists(rewrittenPath), $"Rewritten ISO not found at {rewrittenPath}");
    }

    #endregion
}