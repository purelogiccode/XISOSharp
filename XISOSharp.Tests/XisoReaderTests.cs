using System.Text;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for XisoReader XISO verification, decoding, and extraction functionality.
/// </summary>
[Collection("Sequential")]
public class XisoReaderTests : IDisposable
{
    // Resolved via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal.
    private static readonly string TestIsoPath = TestDataLocator.GetOutputIsoPath();

    private static readonly string InvalidFilePath =
        Path.Combine(TestDataLocator.GetSourceDir(), "binary.bin");

    private static readonly string NonExistentPath =
        Path.Combine(Path.GetTempPath(), $"non_existent_{Guid.NewGuid()}.iso");

    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly bool _savedWarned;
    private readonly long _savedTotalBytes;
    private readonly int _savedTotalFiles;
    private readonly long _savedTotalBytesAllIsos;
    private readonly int _savedTotalFilesAllIsos;
    private readonly bool _savedRemoveSystemUpdate;
    private readonly bool _savedMediaEnable;
    private readonly long _savedXboxDiscLseek;

    public XisoReaderTests()
    {
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        _savedWarned = Logger.Warned;
        _savedTotalBytes = Logger.TotalBytes;
        _savedTotalFiles = Logger.TotalFiles;
        _savedTotalBytesAllIsos = Logger.TotalBytesAllIsos;
        _savedTotalFilesAllIsos = Logger.TotalFilesAllIsos;
        _savedRemoveSystemUpdate = Logger.RemoveSystemUpdate;
        _savedMediaEnable = Logger.MediaEnable;
        _savedXboxDiscLseek = Logger.XboxDiscLseek;

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
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        Logger.Warned = _savedWarned;
        Logger.TotalBytes = _savedTotalBytes;
        Logger.TotalFiles = _savedTotalFiles;
        Logger.TotalBytesAllIsos = _savedTotalBytesAllIsos;
        Logger.TotalFilesAllIsos = _savedTotalFilesAllIsos;
        Logger.RemoveSystemUpdate = _savedRemoveSystemUpdate;
        Logger.MediaEnable = _savedMediaEnable;
        Logger.XboxDiscLseek = _savedXboxDiscLseek;

        // BUG-TEST-008: track every temp dir/file so earlier values are not
        // orphaned when several tests share the fixture instance lifetime.
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Already removed by the test itself.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup: surface nothing, leak nothing trackable.
            }
            catch (IOException)
            {
                // Best-effort cleanup (locked file): nothing further to do here.
            }
        }

        foreach (string file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Parent already removed.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
            catch (IOException)
            {
                // Best-effort cleanup (locked file).
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTempFile(string prefix, string extension)
    {
        string file = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(file);
        return file;
    }

    /// <summary>
    /// Verifies that VerifyXiso returns positive root directory sector and size values for a valid ISO file.
    /// </summary>
    [Fact]
    public void VerifyXiso_ValidFile_ReturnsExpectedValues()
    {
        using FileStream fs = new(TestIsoPath,
            new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });

        (uint rootDirSector, uint rootDirSize, long discLseek) = XisoReader.VerifyXiso(fs, "source.iso");

        Assert.True(rootDirSector > 0);
        Assert.True(rootDirSize > 0);
        Assert.Equal(0, discLseek);
    }

    /// <summary>
    /// Verifies that VerifyXiso throws a FileNotFoundException for a path that does not exist.
    /// </summary>
    [Fact]
    public void VerifyXiso_NonExistentFile_Throws() =>
        Assert.Throws<FileNotFoundException>(static () =>
        {
            using FileStream fs = new(NonExistentPath,
                new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
            XisoReader.VerifyXiso(fs, "missing.iso");
        });

    /// <summary>
    /// Verifies that VerifyXiso throws an IOException when given a file that is not a valid XISO image.
    /// </summary>
    [Fact]
    public void VerifyXiso_InvalidFileNotIso_Throws() =>
        Assert.Throws<IOException>(static () =>
        {
            using FileStream fs = new(InvalidFilePath,
                new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
            XisoReader.VerifyXiso(fs, "binary.bin");
        });

    /// <summary>
    /// Verifies that VerifyXiso throws an IOException when given a large file containing only random data.
    /// </summary>
    [Fact]
    public void VerifyXiso_LargeInvalidFile_Throws()
    {
        string invalidPath = CreateTempFile("xiso_garbage", ".bin");
        try
        {
            byte[] data = new byte[Constants.HeaderOffset + Constants.SectorSize];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(invalidPath, data);

            Assert.Throws<IOException>(() =>
            {
                using FileStream fs = new(invalidPath,
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
        int err = XisoReader.DecodeXiso(TestIsoPath, null, ExtractMode.List, out _, true);
        Assert.Equal(0, err);
    }

    /// <summary>
    /// Verifies that DecodeXiso in Extract mode creates output files in the specified directory.
    /// </summary>
    [Fact]
    public void DecodeXiso_ExtractMode_ExtractsFiles()
    {
        string tempDir = CreateTempDir("xiso_extract_test");

        int err = XisoReader.DecodeXiso(TestIsoPath, tempDir, ExtractMode.Extract, out _, true);

        Assert.Equal(0, err);
        Assert.True(Directory.Exists(tempDir));

        string[] files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
    }

    /// <summary>
    /// Verifies that DecodeXiso in Extract mode without an explicit output directory extracts to an ISO-named subdirectory.
    /// </summary>
    [Fact]
    public void DecodeXiso_ExtractMode_WithoutOutputDir_ExtractsToIsoNamedDir()
    {
        string cwd = Directory.GetCurrentDirectory();
        string tempDir = CreateTempDir("xiso_e2e");
        try
        {
            Directory.SetCurrentDirectory(tempDir);

            int err = XisoReader.DecodeXiso(TestIsoPath, null, ExtractMode.Extract, out _, true);
            Assert.Equal(0, err);

            string extractedDir = Path.Combine(tempDir, "source");
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
        string invalidPath = CreateTempFile("xiso_garbage", ".bin");
        try
        {
            byte[] data = new byte[Constants.HeaderOffset + Constants.SectorSize];
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
    public void DecodeXiso_SmallFile_Throws() => Assert.Throws<IOException>(() =>
        XisoReader.DecodeXiso(InvalidFilePath, null, ExtractMode.List, out _, true));

    /// <summary>
    /// Verifies that DecodeXiso throws a FileNotFoundException for a path that does not exist.
    /// </summary>
    [Fact]
    public void DecodeXiso_NonExistentFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() =>
            XisoReader.DecodeXiso(NonExistentPath, null, ExtractMode.List, out _, true));

    /// <summary>
    /// Verifies that the ExtractErrorException message contains the error code name when constructed with ErrEndOfSector.
    /// </summary>
    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode()
    {
        ExtractErrorException ex = new(ExtractError.ErrEndOfSector);
        Assert.Contains("ErrEndOfSector", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the ExtractErrorException message contains the error code name when constructed with ErrIsoNoFiles.
    /// </summary>
    [Fact]
    public void ExtractErrorException_MessageContainsErrorCode_NoFiles()
    {
        ExtractErrorException ex = new(ExtractError.ErrIsoNoFiles);
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
        string invalidPath = CreateTempFile("xiso_bad_toc", ".bin");
        try
        {
            byte[] magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
            const int fileLength = Constants.HeaderOffset + Constants.HeaderDataLength
                                                          + 4 + 4
                                                          + Constants.FileTimeSize + Constants.UnusedSize
                                                          + Constants.HeaderDataLength;

            byte[] data = new byte[fileLength];
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
                using FileStream fs = new(invalidPath,
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
