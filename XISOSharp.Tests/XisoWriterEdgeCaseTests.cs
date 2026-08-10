namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoWriter"/> edge cases: empty directories,
/// special characters, custom output names, and large file rejection.
/// </summary>
[Collection("Sequential")]
public class XisoWriterEdgeCaseTests : IDisposable
{
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_writer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void CreateXiso_EmptyDirectory_ProducesValidIso()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public void CreateXiso_EmptyDirectory_CanBeAudited()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var auditResult = XisoReader.AuditXiso(isoPath);
        Assert.True(auditResult.IsValid, $"Empty ISO audit failed: {string.Join("; ", auditResult.Issues)}");
    }

    [Fact]
    public void CreateXiso_EmptyDirectory_CanBeListed()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        var listResult = XisoReader.List(isoPath, false);
        Assert.Equal(0, listResult);
    }

    [Fact]
    public void CreateXiso_WithEmptySubdirectory_PreservesIt()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create source with an empty subdirectory
        Directory.CreateDirectory(Path.Combine(srcDir, "empty_subdir"));
        File.WriteAllText(Path.Combine(srcDir, "file.txt"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(Directory.Exists(Path.Combine(extractDir, "empty_subdir")),
            "Empty subdirectory should be preserved");
        Assert.True(File.Exists(Path.Combine(extractDir, "file.txt")));
    }

    [Fact]
    public void CreateXiso_SpecialCharactersInFilename_PreservesThem()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create files with various Latin-1-encodable characters
        File.WriteAllText(Path.Combine(srcDir, "file with spaces.txt"), "spaces");
        File.WriteAllText(Path.Combine(srcDir, "file-with-dashes.txt"), "dashes");
        File.WriteAllText(Path.Combine(srcDir, "file.with.dots.txt"), "dots");
        File.WriteAllText(Path.Combine(srcDir, "FILE.TXT"), "uppercase");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "file with spaces.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "file-with-dashes.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "file.with.dots.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "FILE.TXT")));
    }

    [Fact]
    public void CreateXiso_CustomOutputName_UsesProvidedName()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "test.txt"), "data");

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, "my_custom_name", null);

        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.Contains("my_custom_name", isoPath, StringComparison.Ordinal);
        Assert.DoesNotContain(".iso", isoPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateXiso_DefaultName_AddsIsoExtension()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "test.txt"), "data");

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);

        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        Assert.EndsWith(".iso", isoPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateXiso_ManyFiles_ProducesValidIso()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create 50 files
        for (var i = 0; i < 50; i++)
        {
            File.WriteAllText(Path.Combine(srcDir, $"file_{i:D3}.txt"), $"content_{i}");
        }

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        for (var i = 0; i < 50; i++)
        {
            var path = Path.Combine(extractDir, $"file_{i:D3}.txt");
            Assert.True(File.Exists(path), $"file_{i:D3}.txt missing");
            Assert.Equal($"content_{i}", File.ReadAllText(path));
        }
    }

    [Fact]
    public void CreateXiso_DeeplyNested_PreservesStructure()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create nested structure: a/b/c/d/e/file.txt
        var nestedDir = Path.Combine(srcDir, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "deep.txt"), "deep content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        var extractedPath = Path.Combine(extractDir, "a", "b", "c", "d", "e", "deep.txt");
        Assert.True(File.Exists(extractedPath), "Deeply nested file missing");
        Assert.Equal("deep content", File.ReadAllText(extractedPath));
    }

    [Fact]
    public void CreateXiso_LargeFileContent_PreservesCorrectly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create a 1MB file with known content
        var data = new byte[1024 * 1024];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(Path.Combine(srcDir, "large.bin"), data);

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        var extracted = File.ReadAllBytes(Path.Combine(extractDir, "large.bin"));
        Assert.Equal(data, extracted);
    }

    [Fact]
    public void CreateXiso_BinaryContent_PreservesAllByteValues()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create file with all 256 byte values
        var data = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            data[i] = (byte)i;
        }

        File.WriteAllBytes(Path.Combine(srcDir, "allbytes.bin"), data);

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        var extracted = File.ReadAllBytes(Path.Combine(extractDir, "allbytes.bin"));
        Assert.Equal(data, extracted);
    }

    [Fact]
    public void CreateXiso_ProgressCallback_ReportsFinalTotal()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "file.txt"), "test content");

        long finalBytes = 0;
        long lastCurrent = 0;

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out _, null,
            (current, final) =>
            {
                lastCurrent = current;
                finalBytes = final;
            });

        Assert.True(lastCurrent > 0, "Should have reported progress");
        Assert.True(finalBytes > 0, "Should have reported the final byte total");
    }

    [Fact]
    public void CreateXiso_CancellationDuringWrite_ThrowsOperationCanceled()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        // Create enough files to ensure cancellation hits during write
        for (var i = 0; i < 100; i++)
        {
            File.WriteAllText(Path.Combine(srcDir, $"file_{i}.txt"), new string('x', 10000));
        }

        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoWriter.CreateXiso(srcDir, outputDir, null, null, out _, null, null, cts.Token));
    }

    [Fact]
    public void CreateXiso_SystemUpdate_SkippedWhenEnabled()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // Create $SystemUpdate directory
        var updateDir = Path.Combine(srcDir, "$SystemUpdate");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "update.bin"), "update data");
        File.WriteAllText(Path.Combine(srcDir, "game.txt"), "game data");

        Logger.RemoveSystemUpdate = true;
        try
        {
            XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
            Assert.NotNull(isoPath);

            XisoReader.Extract(isoPath, extractDir, false);

            Assert.True(File.Exists(Path.Combine(extractDir, "game.txt")));
            Assert.False(File.Exists(Path.Combine(extractDir, "$SystemUpdate", "update.bin")),
                "$SystemUpdate should be skipped");
        }
        finally
        {
            Logger.RemoveSystemUpdate = false;
        }
    }

    [Fact]
    public void CreateXiso_MediaEnableDisabled_SkipsPatching()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        // Create a minimal .xbe file (just enough to not crash)
        var xbeContent = new byte[1024];
        Array.Fill(xbeContent, (byte)0x00);
        File.WriteAllBytes(Path.Combine(srcDir, "test.xbe"), xbeContent);
        File.WriteAllText(Path.Combine(srcDir, "readme.txt"), "text");

        Logger.MediaEnable = false;
        try
        {
            var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);
        }
        finally
        {
            Logger.MediaEnable = true;
        }
    }

    /// <summary>
    /// Builds a synthetic .xbe containing the media-enable pattern at the given offsets
    /// and returns the expected patched copy (pattern byte 7 replaced with 0xEB).
    /// </summary>
    private static (byte[] Original, byte[] ExpectedPatched) BuildXbeWithPattern(params int[] offsets)
    {
        var data = new byte[0x00210000]; // ~2.1 MB: larger than the 2 MB read buffer
        Array.Fill(data, (byte)0x41);

        foreach (var offset in offsets)
        {
            Constants.MediaEnable.CopyTo(data, offset);
        }

        var expected = (byte[])data.Clone();
        foreach (var offset in offsets)
        {
            expected[offset + Constants.MediaEnableBytePos] = Constants.MediaEnableByte;
        }

        return (data, expected);
    }

    private static void AssertPatchedBytes(string isoPath, byte[] expected, string xbeRelPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"xiso_mp_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            var result = XisoReader.Extract(isoPath, extractDir, false);
            Assert.Equal(0, result);

            var actual = File.ReadAllBytes(Path.Combine(extractDir, xbeRelPath));
            Assert.Equal(expected, actual);
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
    public void CreateXiso_MediaEnable_PatchesPatternBytesEndToEnd()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        // Offsets: start of file, mid-file, and straddling the 2 MB read-buffer boundary
        // (0x200000) so the Boyer-Moore overlap logic is exercised.
        (byte[] original, byte[] expected) = BuildXbeWithPattern(0, 0x1234, 0x1FFFFC, 0x200004, 0x200100);
        File.WriteAllBytes(Path.Combine(srcDir, "test.xbe"), original);
        File.WriteAllText(Path.Combine(srcDir, "readme.txt"), "text");

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        AssertPatchedBytes(isoPath, expected, "test.xbe");
    }

    [Fact]
    public void CreateXiso_MediaEnableDisabled_LeavesPatternBytesUntouched()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        (byte[] original, _) = BuildXbeWithPattern(0, 0x1234, 0x1FFFFC);
        File.WriteAllBytes(Path.Combine(srcDir, "test.xbe"), original);

        Logger.MediaEnable = false;
        try
        {
            var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
            Assert.Equal(0, result);
            Assert.NotNull(isoPath);

            AssertPatchedBytes(isoPath, original, "test.xbe");
        }
        finally
        {
            Logger.MediaEnable = true;
        }
    }

    [Fact]
    public void CreateXiso_MediaEnable_IgnoresNonXbeFiles()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        (byte[] original, _) = BuildXbeWithPattern(0);
        File.WriteAllBytes(Path.Combine(srcDir, "not_an_xbe.bin"), original);

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        AssertPatchedBytes(isoPath, original, "not_an_xbe.bin");
    }

    [Fact]
    public void CreateXiso_MediaEnable_IgnoresXexFiles()
    {
        // Xbox 360 executables are never patched: the media-enable patch is an XBE-only
        // concept (issue #28) — a .xex containing the XBE pattern must be copied verbatim.
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();

        (byte[] original, _) = BuildXbeWithPattern(0, 0x2000);
        File.WriteAllBytes(Path.Combine(srcDir, "default.xex"), original);

        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        AssertPatchedBytes(isoPath, original, "default.xex");
    }
}
