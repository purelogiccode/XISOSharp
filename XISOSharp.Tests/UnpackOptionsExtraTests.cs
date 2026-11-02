using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="UnpackOptions"/>: skip decisions across
/// filesystems, failure recording, and the end-of-run summary throw.
/// </summary>
public class UnpackOptionsExtraTests
{
    /// <summary>Verifies the default option values.</summary>
    [Fact]
    public void Defaults_AreOff()
    {
        UnpackOptions options = new();
        Assert.False(options.SkipExisting);
        Assert.False(options.ContinueOnError);
    }

    /// <summary>Verifies skip decisions require <see cref="UnpackOptions.SkipExisting"/>.</summary>
    [Fact]
    public void ShouldSkip_Disabled_ReturnsFalse()
    {
        MemoryFilesystem fs = new();
        using (Stream stream = fs.CreateFile("f.bin"))
        {
            stream.Write(new byte[10]);
        }

        UnpackOptions options = new();
        Assert.False(options.ShouldSkip("f.bin", 10, fs));
    }

    /// <summary>Verifies skip decisions compare against the destination filesystem size.</summary>
    [Fact]
    public void ShouldSkip_Enabled_ComparesSize()
    {
        MemoryFilesystem fs = new();
        using (Stream stream = fs.CreateFile("f.bin"))
        {
            stream.Write(new byte[10]);
        }

        UnpackOptions options = new() { SkipExisting = true };
        Assert.True(options.ShouldSkip("f.bin", 10, fs));
        Assert.False(options.ShouldSkip("f.bin", 11, fs));
        Assert.False(options.ShouldSkip("missing.bin", 10, fs));
    }

    /// <summary>Verifies blank paths and negative sizes are never skipped.</summary>
    [Theory]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("f.bin", -1)]
    public void ShouldSkip_InvalidInputs_ReturnFalse(string path, long size)
    {
        UnpackOptions options = new() { SkipExisting = true };
        Assert.False(options.ShouldSkip(path, size, new MemoryFilesystem()));
    }

    /// <summary>Verifies no failures means no summary throw.</summary>
    [Fact]
    public void ThrowIfFailed_NoFailures_DoesNotThrow()
    {
        UnpackOptions options = new();
        options.ThrowIfFailed("game.iso");
    }

    /// <summary>Verifies recorded failures surface in the summary throw.</summary>
    [Fact]
    public void ThrowIfFailed_WithFailures_ThrowsSummary()
    {
        UnpackOptions options = new() { ContinueOnError = true };
        options.RecordFailure(new ExtractFileException(ExtractError.ErrExtractFailed, "first failed"));
        options.RecordFailure(new ExtractFileException(ExtractError.ErrExtractFailed, "second failed"));

        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() => options.ThrowIfFailed("game.iso"));

        Assert.Contains("game.iso", ex.Message, StringComparison.Ordinal);
        Assert.Contains("first failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("second failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
    }

    /// <summary>Verifies local-disk skip probing uses the default filesystem overload.</summary>
    [Fact]
    public void ShouldSkip_LocalPath_Works()
    {
        string file = Path.Combine(Path.GetTempPath(), $"xiso_skip_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(file, new byte[7]);
            UnpackOptions options = new() { SkipExisting = true };
            Assert.True(options.ShouldSkip(file, 7));
            Assert.False(options.ShouldSkip(file, 8));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Verifies block-device based destination probing works.</summary>
    [Fact]
    public void ShouldSkip_BlockDeviceFilesystem_Works()
    {
        MemoryFilesystem fs = new();
        using (Stream stream = fs.CreateFile("x.bin"))
        {
            stream.Write(new byte[3]);
        }

        UnpackOptions options = new() { SkipExisting = true };
        Assert.True(options.ShouldSkip("x.bin", 3, fs));
    }

    /// <summary>Verifies validation issue records expose their fields and support <c>with</c>.</summary>
    [Fact]
    public void ValidationIssue_RecordContract()
    {
        byte[] hash = [1, 2, 3];
        ValidationIssue issue = new(ValidationIssueType.ChecksumMismatch, "dir/file.bin", 10, 11, hash, null);

        Assert.Equal(ValidationIssueType.ChecksumMismatch, issue.Type);
        Assert.Equal("dir/file.bin", issue.Path);
        Assert.Equal(10, issue.SourceSize);
        Assert.Equal(11, issue.OutputSize);
        Assert.Same(hash, issue.SourceHash);
        Assert.Null(issue.OutputHash);

        ValidationIssue changed = issue with { Path = "other.bin" };
        Assert.Equal("other.bin", changed.Path);
        Assert.Equal(issue.Type, changed.Type);
    }

    /// <summary>Verifies validation results carry their issue list.</summary>
    [Fact]
    public void ValidationResult_CarriesIssues()
    {
        ValidationIssue issue = new(ValidationIssueType.MissingInOutput, "missing.bin", 5, 0, null, null);
        ValidationResult result = new(false, 2, 1, 1, 1, 15, 10, [issue]);

        Assert.False(result.Passed);
        Assert.Equal(2, result.SourceFileCount);
        Assert.Equal(1, result.OutputFileCount);
        Assert.Single(result.Issues);
        Assert.Equal("missing.bin", result.Issues[0].Path);
    }
}
