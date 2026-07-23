using XISOSharp;

namespace XISOSharp.Tests;

[Collection("Sequential")]
public class XisoReaderTests : IDisposable
{
    private static readonly string TestIsoPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "output", "source.iso"));

    private static readonly string InvalidFilePath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "source", "binary.bin"));

    private static readonly string NonExistentPath =
        Path.Combine(Path.GetTempPath(), $"non_existent_{Guid.NewGuid()}.iso");

    private string _tempDir = "";

    public XisoReaderTests()
    {
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        Logger.Warned = false;
        Logger.TotalBytes = 0;
        Logger.TotalFiles = 0;
        Logger.TotalBytesAllIsos = 0;
        Logger.TotalFilesAllIsos = 0;
        Logger.RemoveSystemUpdate = false;
        Logger.MediaEnable = true;
        Logger.XboxDiscLseek = 0;
    }

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* cleanup best-effort */ }
        }
    }

    [Fact]
    public void VerifyXiso_ValidFile_ReturnsExpectedValues()
    {
        using var fs = new FileStream(TestIsoPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read
        });

        var (rootDirSector, rootDirSize, discLseek) = XisoReader.VerifyXiso(fs, "source.iso");

        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal(0, discLseek);
    }

    [Fact]
    public void VerifyXiso_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
        {
            using var fs = new FileStream(NonExistentPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read
            });
            XisoReader.VerifyXiso(fs, "missing.iso");
        });
    }

    [Fact]
    public void VerifyXiso_InvalidFileNotIso_Throws()
    {
        Assert.Throws<IOException>(() =>
        {
            using var fs = new FileStream(InvalidFilePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read
            });
            XisoReader.VerifyXiso(fs, "binary.bin");
        });
    }

    [Fact]
    public void VerifyXiso_LargeInvalidFile_Throws()
    {
        string invalidPath = Path.Combine(Path.GetTempPath(), $"garbage_{Guid.NewGuid()}.bin");
        try
        {
            var data = new byte[Constants.HeaderOffset + Constants.SectorSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<IOException>(() =>
            {
                using var fs = new FileStream(invalidPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read
                });
                XisoReader.VerifyXiso(fs, "garbage.bin");
            });
        }
        finally
        {
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
        }
    }

    [Fact]
    public void DecodeXiso_ListMode_ReturnsSuccess()
    {
        var err = XisoReader.DecodeXiso(TestIsoPath, null, ExtractMode.List, out _, true);
        Assert.Equal(0, err);
    }

    [Fact]
    public void DecodeXiso_ExtractMode_ExtractsFiles()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"xiso_extract_test_{Guid.NewGuid()}");

        var err = XisoReader.DecodeXiso(TestIsoPath, _tempDir, ExtractMode.Extract, out _, true);

        Assert.Equal(0, err);
        Assert.True(Directory.Exists(_tempDir));

        var files = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
    }

    [Fact]
    public void DecodeXiso_ExtractMode_WithoutOutputDir_ExtractsToIsoNamedDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"xiso_e2e_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);
            Directory.SetCurrentDirectory(_tempDir);

            var err = XisoReader.DecodeXiso(TestIsoPath, null, ExtractMode.Extract, out _, true);
            Assert.Equal(0, err);

            var extractedDir = Path.Combine(_tempDir, "source");
            Assert.True(Directory.Exists(extractedDir));
            Assert.NotEmpty(Directory.GetFiles(extractedDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void DecodeXiso_LargeInvalidFile_Throws()
    {
        string invalidPath = Path.Combine(Path.GetTempPath(), $"xiso_garbage_{Guid.NewGuid()}.bin");
        try
        {
            var data = new byte[Constants.HeaderOffset + Constants.SectorSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<IOException>(() =>
            {
                XisoReader.DecodeXiso(invalidPath, null, ExtractMode.List, out _, true);
            });
        }
        finally
        {
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
        }
    }

    [Fact]
    public void DecodeXiso_SmallFile_Throws()
    {
        Assert.Throws<IOException>(() =>
        {
            XisoReader.DecodeXiso(InvalidFilePath, null, ExtractMode.List, out _, true);
        });
    }

    [Fact]
    public void DecodeXiso_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
        {
            XisoReader.DecodeXiso(NonExistentPath, null, ExtractMode.List, out _, true);
        });
    }

    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode()
    {
        var ex = new ExtractErrorException(ExtractError.ErrEndOfSector);
        Assert.Contains("ErrEndOfSector", ex.Message);
    }

    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode_NoFiles()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.Contains("ErrIsoNoFiles", ex.Message);
    }
}
