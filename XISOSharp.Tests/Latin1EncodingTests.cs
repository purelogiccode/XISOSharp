namespace XISOSharp.Tests;

/// <summary>
/// Tests for Latin-1 encoding behavior through the public XISO API.
/// Verifies that filenames with extended byte values (0x80-0xFF) survive
/// create → extract round-trips without being replaced by '?'.
/// </summary>
[Collection("Sequential")]
public class Latin1EncodingTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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
        var dir = Path.Combine(Path.GetTempPath(), $"xiso_latin1_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void CreateExtract_AsciiFilename_PreservesExactly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "hello.txt"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "hello.txt")));
    }

    [Fact]
    public void CreateExtract_FilenameWithSpaces_PreservesExactly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "file with spaces.txt"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "file with spaces.txt")));
    }

    [Fact]
    public void CreateExtract_FilenameWithDots_PreservesExactly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "file.name.with.dots.txt"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "file.name.with.dots.txt")));
    }

    [Fact]
    public void CreateExtract_FilenameWithDashes_PreservesExactly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "my-file_name.txt"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "my-file_name.txt")));
    }

    [Fact]
    public void CreateExtract_UppercaseFilename_PreservesCase()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "README.TXT"), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "README.TXT")));
    }

    [Fact]
    public void CreateExtract_LongFilename_PreservesExactly()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // 40-char filename (under 42-byte XISO limit for some implementations)
        const string longName = "this_is_a_filename_that_is_quite_long.txt";
        File.WriteAllText(Path.Combine(srcDir, longName), "content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, longName)),
            $"Long filename '{longName}' not preserved");
    }

    [Fact]
    public void CreateExtract_NonAsciiLatin1_RoundTrips()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        // 0xE9 = é in Latin-1
        var nonAsciiName = "café" + (char)0xE9 + ".txt";
        File.WriteAllText(Path.Combine(srcDir, nonAsciiName), "accented content");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        var extractedFiles = Directory.GetFiles(extractDir);
        Assert.Single(extractedFiles);
        Assert.Equal(nonAsciiName, Path.GetFileName(extractedFiles[0]));
        Assert.Equal("accented content", File.ReadAllText(extractedFiles[0]));
    }

    [Fact]
    public void CreateExtract_FileContent_PreservesAllByteValues()
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
    public void CreateExtract_EmptyFile_PreservesZeroLength()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        File.WriteAllText(Path.Combine(srcDir, "empty.txt"), "");

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        Assert.True(File.Exists(Path.Combine(extractDir, "empty.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(extractDir, "empty.txt")).Length);
    }

    [Fact]
    public void CreateExtract_MultipleFilesWithMixedNames_AllPreserved()
    {
        var srcDir = CreateTempDir();
        var outputDir = CreateTempDir();
        var extractDir = CreateTempDir();

        var names = new[]
        {
            "normal.txt", "with spaces.txt", "with-dashes.txt", "with.dots.txt", "UPPERCASE.TXT", "MiXeD.CaSe", "123numeric.txt"
        };

        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(srcDir, name), $"content of {name}");
        }

        XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.NotNull(isoPath);

        XisoReader.Extract(isoPath, extractDir, false);

        foreach (var name in names)
        {
            var path = Path.Combine(extractDir, name);
            Assert.True(File.Exists(path), $"File '{name}' not found after extract");
            Assert.Equal($"content of {name}", File.ReadAllText(path));
        }
    }
}
