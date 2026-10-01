namespace XISOSharp.Tests;

/// <summary>
/// Refusal-contract tests for <see cref="XisoRepairer.RepairInPlace"/>: invalid paths,
/// missing files, CISO/split containers, and non-XISO inputs, plus
/// <see cref="XisoSalvager.DefaultOutputPath"/> naming.
/// </summary>
public class XisoRepairerRefusalTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>Removes temporary directories.</summary>
    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_repair_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Verifies null and empty paths are rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RepairInPlace_InvalidPath_Throws(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => XisoRepairer.RepairInPlace(path!));
    }

    /// <summary>Verifies a missing file is reported as not found.</summary>
    [Fact]
    public void RepairInPlace_MissingFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(CreateTempDir(), "missing.iso");
        Assert.Throws<FileNotFoundException>(() => XisoRepairer.RepairInPlace(missing));
    }

    /// <summary>Verifies CISO containers are refused before any file access.</summary>
    [Fact]
    public void RepairInPlace_CsoExtension_Refused()
    {
        string path = Path.Combine(CreateTempDir(), "game.cso");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => XisoRepairer.RepairInPlace(path));
        Assert.Contains("decompress", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies split parts are refused before any file access.</summary>
    [Fact]
    public void RepairInPlace_SplitPart_Refused()
    {
        string path = Path.Combine(CreateTempDir(), "game.1.iso");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => XisoRepairer.RepairInPlace(path));
        Assert.Contains("reassemble", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies non-XISO data is refused with a format error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void RepairInPlace_NotAnXiso_ThrowsFormatError(int size)
    {
        string path = Path.Combine(CreateTempDir(), "not.iso");
        File.WriteAllBytes(path, new byte[size]);
        Assert.ThrowsAny<XisoFormatException>(() => XisoRepairer.RepairInPlace(path));
    }

    /// <summary>Verifies default salvage output keeps the directory and swaps the extension.</summary>
    [Fact]
    public void Salvage_DefaultOutputPath_Naming()
    {
        string source = Path.Combine(Path.GetTempPath(), "sub", "game.iso");
        string expected = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "game.salvaged.iso");
        Assert.Equal(expected, XisoSalvager.DefaultOutputPath(source));

        string cso = Path.Combine(Path.GetTempPath(), "sub", "game.cso");
        string expectedCso = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cso))!, "game.salvaged.iso");
        Assert.Equal(expectedCso, XisoSalvager.DefaultOutputPath(cso));
    }

    /// <summary>Verifies default salvage output rejects invalid paths.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Salvage_DefaultOutputPath_Invalid_Throws(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => XisoSalvager.DefaultOutputPath(path!));
    }
}
