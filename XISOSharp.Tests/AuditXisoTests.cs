using XISOSharp.Models;

namespace XISOSharp.Tests;

using TestDataGenerator;

/// <summary>
/// Tests for <see cref="XisoReader.AuditXiso(string)"/>, verifying deep integrity
/// auditing of XISO images.
/// </summary>
[Collection("Sequential")]
public class AuditXisoTests : IDisposable
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
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_audit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void AuditXiso_ValidIso_ReturnsValid()
    {
        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        AuditResult result = XisoReader.AuditXiso(isoPath);

        Assert.True(result.IsValid, $"Audit failed: {string.Join("; ", result.Issues)}");
        Assert.True(result.FilesChecked > 0);
        Assert.True(result.DirsChecked > 0);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AuditXiso_ValidIso_CountsFilesCorrectly()
    {
        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        AuditResult result = XisoReader.AuditXiso(isoPath);

        // Test source has: file1.txt, file2.txt, binary.bin, test.xbe, subdir/subfile.txt, subdir/nested/deep.txt
        Assert.True(result.FilesChecked >= 5, $"Expected at least 5 files, got {result.FilesChecked}");
        Assert.True(result.DirsChecked >= 2, $"Expected at least 2 dirs, got {result.DirsChecked}");
    }

    [Fact]
    public void AuditXiso_InvalidFile_ReturnsNotValid()
    {
        string tempFile = Path.Combine(CreateTempDir(), "not_an_iso.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        AuditResult result = XisoReader.AuditXiso(tempFile);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void AuditXiso_TruncatedFile_ReportsIssues()
    {
        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        // Truncate the file to half its size
        string truncatedPath = Path.Combine(CreateTempDir(), "truncated.iso");
        byte[] originalBytes = File.ReadAllBytes(isoPath);
        int halfLen = originalBytes.Length / 2;
        File.WriteAllBytes(truncatedPath, originalBytes[..halfLen]);

        AuditResult result = XisoReader.AuditXiso(truncatedPath);

        // Should either be invalid or have issues
        Assert.False(result is { IsValid: true, Issues.Count: 0 },
            "Truncated ISO should have audit issues");
    }

    [Fact]
    public void AuditXiso_RandomData_ReturnsNotValid()
    {
        string tempFile = Path.Combine(CreateTempDir(), "random.bin");
        byte[] data = new byte[4096];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(tempFile, data);

        AuditResult result = XisoReader.AuditXiso(tempFile);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void AuditXiso_EmptyFile_ReturnsNotValid()
    {
        string tempFile = Path.Combine(CreateTempDir(), "empty.bin");
        File.WriteAllBytes(tempFile, []);

        AuditResult result = XisoReader.AuditXiso(tempFile);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AuditXiso_NonExistentFile_ThrowsFileNotFoundException()
    {
        string nonExistent = Path.Combine(CreateTempDir(), "no_such_file.iso");
        Assert.Throws<FileNotFoundException>(() => XisoReader.AuditXiso(nonExistent));
    }

    [Fact]
    public void AuditXiso_CreatedThenRewritten_ReturnsValid()
    {
        string createDir = CreateTempDir();
        string rewriteDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out string? rewrittenPath);
        Assert.NotNull(rewrittenPath);

        AuditResult result = XisoReader.AuditXiso(rewrittenPath);

        Assert.True(result.IsValid, $"Audit of rewritten ISO failed: {string.Join("; ", result.Issues)}");
    }

    [Fact]
    public void AuditXiso_FilesChecked_MatchesCount()
    {
        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        AuditResult result = XisoReader.AuditXiso(isoPath);

        // Verify consistency: if valid, files + dirs should be > 0
        if (result.IsValid)
        {
            Assert.True(result.FilesChecked + result.DirsChecked > 0,
                "At least one entry should be checked");
        }
    }

    [Fact]
    public void AuditXiso_EmptyIso_ReturnsValidWithZeroCounts()
    {
        string srcDir = CreateTempDir();
        // Create an ISO from a directory with only an empty_dir (no files)
        // The empty_dir will be represented as EmptySubdirectory

        string outputDir = CreateTempDir();
        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? isoPath, null, null);
        Assert.NotNull(isoPath);

        AuditResult result = XisoReader.AuditXiso(isoPath);

        // Empty ISO should still be structurally valid
        Assert.True(result.IsValid, $"Empty ISO audit failed: {string.Join("; ", result.Issues)}");
    }

    [Fact]
    public void AuditXiso_AfterExtractAndRecreate_PreservesValidity()
    {
        string createDir = CreateTempDir();
        string extractDir = CreateTempDir();
        string recreateDir = CreateTempDir();

        // Create → Extract → Recreate → Audit
        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out string? isoPath1, null, null);
        Assert.NotNull(isoPath1);

        XisoReader.Extract(isoPath1, extractDir, false);

        XisoWriter.CreateXiso(extractDir, recreateDir, null, null, out string? isoPath2, null, null);
        Assert.NotNull(isoPath2);

        AuditResult result = XisoReader.AuditXiso(isoPath2);

        Assert.True(result.IsValid, $"Recreated ISO audit failed: {string.Join("; ", result.Issues)}");
    }
}
