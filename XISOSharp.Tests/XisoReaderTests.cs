using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for XisoReader XISO verification, decoding, and extraction functionality.
/// </summary>
[Collection("Sequential")]
public class XisoReaderTests : IDisposable
{
    private static readonly string TestIsoPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "output",
            "source.iso"));

    private static readonly string InvalidFilePath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "source",
            "binary.bin"));

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
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                /* cleanup best-effort */
            }
        }
    }

    /// <summary>
    /// Verifies that VerifyXiso returns positive root directory sector and size values for a valid ISO file.
    /// </summary>
    [Fact]
    public void VerifyXiso_ValidFile_ReturnsExpectedValues()
    {
        using var fs = new FileStream(TestIsoPath,
            new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });

        (var rootDirSector, var rootDirSize, var discLseek) = XisoReader.VerifyXiso(fs, "source.iso");

        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal(0, discLseek);
    }

    /// <summary>
    /// Verifies that VerifyXiso throws a FileNotFoundException for a path that does not exist.
    /// </summary>
    [Fact]
    public void VerifyXiso_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(static () =>
        {
            using var fs = new FileStream(NonExistentPath,
                new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
            XisoReader.VerifyXiso(fs, "missing.iso");
        });
    }

    /// <summary>
    /// Verifies that VerifyXiso throws an IOException when given a file that is not a valid XISO image.
    /// </summary>
    [Fact]
    public void VerifyXiso_InvalidFileNotIso_Throws()
    {
        Assert.Throws<IOException>(static () =>
        {
            using var fs = new FileStream(InvalidFilePath,
                new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
            XisoReader.VerifyXiso(fs, "binary.bin");
        });
    }

    /// <summary>
    /// Verifies that VerifyXiso throws an IOException when given a large file containing only random data.
    /// </summary>
    [Fact]
    public void VerifyXiso_LargeInvalidFile_Throws()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"garbage_{Guid.NewGuid()}.bin");
        try
        {
            var data = new byte[Constants.HeaderOffset + Constants.SectorSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<IOException>(() =>
            {
                using var fs = new FileStream(invalidPath,
                    new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
                XisoReader.VerifyXiso(fs, "garbage.bin");
            });
        }
        finally
        {
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
        }
    }

    /// <summary>
    /// Verifies that DecodeXiso in List mode returns a success error code for a valid ISO file.
    /// </summary>
    [Fact]
    public void DecodeXiso_ListMode_ReturnsSuccess()
    {
        var err = XisoReader.DecodeXiso(TestIsoPath, null, ExtractMode.List, out _, true);
        Assert.Equal(0, err);
    }

    /// <summary>
    /// Verifies that DecodeXiso in Extract mode creates output files in the specified directory.
    /// </summary>
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

    /// <summary>
    /// Verifies that DecodeXiso in Extract mode without an explicit output directory extracts to an ISO-named subdirectory.
    /// </summary>
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

    /// <summary>
    /// Verifies that DecodeXiso throws an IOException when given a large file containing only random data.
    /// </summary>
    [Fact]
    public void DecodeXiso_LargeInvalidFile_Throws()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"xiso_garbage_{Guid.NewGuid()}.bin");
        try
        {
            var data = new byte[Constants.HeaderOffset + Constants.SectorSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<IOException>(() => XisoReader.DecodeXiso(invalidPath, null, ExtractMode.List, out _, true));
        }
        finally
        {
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
        }
    }

    /// <summary>
    /// Verifies that DecodeXiso throws an IOException when given a small non-ISO binary file.
    /// </summary>
    [Fact]
    public void DecodeXiso_SmallFile_Throws()
    {
        Assert.Throws<IOException>(() => XisoReader.DecodeXiso(InvalidFilePath, null, ExtractMode.List, out _, true));
    }

    /// <summary>
    /// Verifies that DecodeXiso throws a FileNotFoundException for a path that does not exist.
    /// </summary>
    [Fact]
    public void DecodeXiso_NonExistentFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            XisoReader.DecodeXiso(NonExistentPath, null, ExtractMode.List, out _, true));
    }

    /// <summary>
    /// Verifies that the ExtractErrorException message contains the error code name when constructed with ErrEndOfSector.
    /// </summary>
    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode()
    {
        var ex = new ExtractErrorException(ExtractError.ErrEndOfSector);
        Assert.Contains("ErrEndOfSector", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the ExtractErrorException message contains the error code name when constructed with ErrIsoNoFiles.
    /// </summary>
    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode_NoFiles()
    {
        var ex = new ExtractErrorException(ExtractError.ErrIsoNoFiles);
        Assert.Contains("ErrIsoNoFiles", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that VerifyXiso throws XisoFormatException (not OutOfMemoryException)
    /// when the header contains a valid magic but an absurdly large rootDirSize that
    /// exceeds the available space in the file.
    /// </summary>
    [Fact]
    public void VerifyXiso_InsaneRootDirSize_ThrowsXisoFormatException()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"xiso_bad_toc_{Guid.NewGuid()}.bin");
        try
        {
            var magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
            const int fileLength = Constants.HeaderOffset + Constants.HeaderDataLength
                                                          + 4 + 4
                                                          + Constants.FileTimeSize + Constants.UnusedSize
                                                          + Constants.HeaderDataLength;

            var data = new byte[fileLength];
            Array.Copy(magic, 0, data, Constants.HeaderOffset, magic.Length);

            const int sectorOffset = Constants.HeaderOffset + Constants.HeaderDataLength;
            data[sectorOffset] = 0x08;
            data[sectorOffset + 1] = 0x01;
            data[sectorOffset + 2] = 0x00;
            data[sectorOffset + 3] = 0x00;

            const int sizeOffset = sectorOffset + 4;
            data[sizeOffset] = 0x00;
            data[sizeOffset + 1] = 0x00;
            data[sizeOffset + 2] = 0x00;
            data[sizeOffset + 3] = 0x10;

            const int trailingOffset = Constants.HeaderOffset + Constants.HeaderDataLength
                                                              + 4 + 4
                                                              + Constants.FileTimeSize + Constants.UnusedSize;
            Array.Copy(magic, 0, data, trailingOffset, magic.Length);

            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<XisoFormatException>(() =>
            {
                using var fs = new FileStream(invalidPath,
                    new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
                XisoReader.VerifyXiso(fs, "bad_toc.iso");
            });
        }
        finally
        {
            if (File.Exists(invalidPath))
                File.Delete(invalidPath);
        }
    }
}