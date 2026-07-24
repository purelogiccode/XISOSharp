namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoValidator.ValidateConversion"/>, verifying post-conversion
/// validation comparing source and output XISO images.
/// </summary>
[Collection("Sequential")]
public class XisoValidatorTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_val_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIsoFromSource()
    {
        var outputDir = CreateTempDir();
        XisoWriter.CreateXiso(SourceDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void ValidateConversion_SameIso_Passes()
    {
        var isoPath = CreateIsoFromSource();

        var result = XisoValidator.ValidateConversion(isoPath, isoPath);

        Assert.True(result.Passed, $"Validation failed: {string.Join("; ", result.Issues)}");
        Assert.Equal(result.SourceFileCount, result.OutputFileCount);
        Assert.Equal(result.SourceTotalBytes, result.OutputTotalBytes);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateConversion_SameIso_Checksums_Passes()
    {
        var isoPath = CreateIsoFromSource();

        var result = XisoValidator.ValidateConversion(isoPath, isoPath, true);

        Assert.True(result.Passed, $"Validation with checksums failed: {string.Join("; ", result.Issues)}");
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateConversion_CreateThenRewrite_Passes()
    {
        var createDir = CreateTempDir();
        var rewriteDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath);
        Assert.NotNull(rewrittenPath);

        var result = XisoValidator.ValidateConversion(isoPath, rewrittenPath);

        Assert.True(result.Passed,
            $"Validation of rewrite failed: {string.Join("; ", result.Issues.Select(static i => $"{i.Type}: {i.Path}"))}");
        Assert.Equal(result.SourceFileCount, result.OutputFileCount);
    }

    [Fact]
    public void ValidateConversion_CreateThenRewrite_Checksums_Passes()
    {
        var createDir = CreateTempDir();
        var rewriteDir = CreateTempDir();

        XisoWriter.CreateXiso(SourceDir, createDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Rewrite(isoPath, rewriteDir, out var rewrittenPath);
        Assert.NotNull(rewrittenPath);

        var result = XisoValidator.ValidateConversion(isoPath, rewrittenPath, true);

        Assert.True(result.Passed,
            $"Validation of rewrite with checksums failed: {string.Join("; ", result.Issues.Select(static i => $"{i.Type}: {i.Path}"))}");
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateConversion_DifferentIsos_DetectsDifferences()
    {
        var iso1 = CreateIsoFromSource();

        // Create a second ISO with fewer files
        var partialDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(partialDir, "partial"));
        File.WriteAllText(Path.Combine(partialDir, "partial", "only_file.txt"), "hello");
        var partialIsoDir = CreateTempDir();
        XisoWriter.CreateXiso(Path.Combine(partialDir, "partial"), partialIsoDir, null, null, out var iso2, null, null);
        Assert.NotNull(iso2);

        var result = XisoValidator.ValidateConversion(iso1, iso2);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Issues);
        Assert.Contains(result.Issues, static i => i.Type == ValidationIssueType.MissingInOutput);
    }

    [Fact]
    public void ValidateConversion_FileCounts_AreCorrect()
    {
        var isoPath = CreateIsoFromSource();

        var result = XisoValidator.ValidateConversion(isoPath, isoPath);

        // Test source has: file1.txt, file2.txt, binary.bin, test.xbe, subdir/subfile.txt, subdir/nested/deep.txt
        Assert.True(result.SourceFileCount >= 5, $"Expected at least 5 files, got {result.SourceFileCount}");
        Assert.True(result.SourceDirCount >= 2, $"Expected at least 2 dirs, got {result.SourceDirCount}");
    }

    [Fact]
    public void ValidateConversion_TotalBytes_AreCorrect()
    {
        var isoPath = CreateIsoFromSource();

        var result = XisoValidator.ValidateConversion(isoPath, isoPath);

        Assert.True(result.SourceTotalBytes > 0, "Total bytes should be positive");
        Assert.Equal(result.SourceTotalBytes, result.OutputTotalBytes);
    }

    [Fact]
    public void ValidateConversion_InvalidOutput_ThrowsException()
    {
        var isoPath = CreateIsoFromSource();
        var tempFile = Path.Combine(CreateTempDir(), "not_an_iso.bin");
        File.WriteAllBytes(tempFile, new byte[1024]);

        Assert.ThrowsAny<Exception>(() => XisoValidator.ValidateConversion(isoPath, tempFile));
    }

    [Fact]
    public void ValidateConversion_NonExistentSource_ThrowsException()
    {
        var isoPath = CreateIsoFromSource();
        var nonExistent = Path.Combine(CreateTempDir(), "no_such_file.iso");

        Assert.ThrowsAny<Exception>(() => XisoValidator.ValidateConversion(nonExistent, isoPath));
    }

    [Fact]
    public void LogResult_Passed_OutputsPassMessage()
    {
        var isoPath = CreateIsoFromSource();
        var result = XisoValidator.ValidateConversion(isoPath, isoPath);

        // Should not throw
        XisoValidator.LogResult(result, isoPath, isoPath);
    }

    [Fact]
    public void WriteReport_CreatesValidJson()
    {
        var isoPath = CreateIsoFromSource();
        var result = XisoValidator.ValidateConversion(isoPath, isoPath);
        var reportPath = Path.Combine(CreateTempDir(), "report.json");

        XisoValidator.WriteReport(result, isoPath, isoPath, reportPath);

        Assert.True(File.Exists(reportPath), "Report file should exist");
        var json = File.ReadAllText(reportPath);
        Assert.Contains("\"passed\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"fileCount\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReport_WithIssues_IncludesIssueDetails()
    {
        var iso1 = CreateIsoFromSource();

        var partialDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(partialDir, "partial"));
        File.WriteAllText(Path.Combine(partialDir, "partial", "only_file.txt"), "hello");
        var partialIsoDir = CreateTempDir();
        XisoWriter.CreateXiso(Path.Combine(partialDir, "partial"), partialIsoDir, null, null, out var iso2, null, null);
        Assert.NotNull(iso2);

        var result = XisoValidator.ValidateConversion(iso1, iso2);
        var reportPath = Path.Combine(CreateTempDir(), "report_issues.json");

        XisoValidator.WriteReport(result, iso1, iso2, reportPath);

        var json = File.ReadAllText(reportPath);
        Assert.Contains("\"passed\": false", json, StringComparison.Ordinal);
        Assert.Contains("MissingInOutput", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConversion_ExitCode_Passed_ReturnsZero()
    {
        var isoPath = CreateIsoFromSource();
        var result = XisoValidator.ValidateConversion(isoPath, isoPath);

        Assert.Equal(0, result.Passed ? 0 : 2);
    }

    [Fact]
    public void ValidateConversion_ExitCode_Failed_ReturnsTwo()
    {
        var iso1 = CreateIsoFromSource();

        var partialDir = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(partialDir, "partial"));
        File.WriteAllText(Path.Combine(partialDir, "partial", "only_file.txt"), "hello");
        var partialIsoDir = CreateTempDir();
        XisoWriter.CreateXiso(Path.Combine(partialDir, "partial"), partialIsoDir, null, null, out var iso2, null, null);
        Assert.NotNull(iso2);

        var result = XisoValidator.ValidateConversion(iso1, iso2);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Passed ? 0 : 2);
    }
}
