using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <see cref="XisoReader.VerifyXiso(Stream, string, int?)"/> partition-base
/// probe chain (plain XISO, XGD2/Redump-360, XGD3, XGD2-hybrid, XGD1).
/// Regression cover for the dropped-<c>discLseek</c> bug where a magic match at the
/// global (XGD2) base verified successfully but returned lseek 0, sending every later
/// read to the video partition instead of the game partition.
/// </summary>
[Collection("Sequential")]
public sealed class XisoVerifyProbeTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public XisoVerifyProbeTests()
    {
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        Logger.Warned = false;
    }

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (var f in _tempFiles)
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    /// <summary>
    /// Writes a minimal valid XISO volume header (magic + root sector/size + trailing
    /// magic) at <c>partitionBase + HeaderOffset</c> in a sparse temp file and returns
    /// its path. The file always spans every probe offset (up to the XGD2-hybrid base):
    /// short reads throw <see cref="IOException"/> instead of cleanly missing, so a
    /// truncated image could never exercise the later probes. Unwritten ranges read
    /// back as zeros without allocating disk.
    /// </summary>
    private string CreateProbeImage(long partitionBase, uint rootSector = 0x108, uint rootSize = 2048)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xiso_probe_{Guid.NewGuid():N}.iso");
        var magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
        var headerLength = Constants.HeaderOffset + Constants.HeaderDataLength + 4 + 4
            + Constants.FileTimeSize + Constants.UnusedSize + Constants.HeaderDataLength;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.SetLength((long)Constants.Xgd2HybridLseekOffset + headerLength + Constants.SectorSize);
            fs.Seek(partitionBase + Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(magic, 0, magic.Length);
            Span<byte> intBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(intBuf, rootSector);
            fs.Write(intBuf);
            BinaryPrimitives.WriteUInt32LittleEndian(intBuf, rootSize);
            fs.Write(intBuf);
            fs.Seek(Constants.FileTimeSize + Constants.UnusedSize, SeekOrigin.Current);
            fs.Write(magic, 0, magic.Length);
        }

        _tempFiles.Add(path);
        return path;
    }

    private static (uint rootSector, uint rootSize, long discLseek) Verify(string path)
    {
        using var fs = new FileStream(path,
            new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read });
        return XisoReader.VerifyXiso(fs, Path.GetFileName(path));
    }

    [Fact]
    public void VerifyXiso_PlainBaseZero_ReturnsZeroLseek()
    {
        var (rootSector, rootSize, discLseek) = Verify(CreateProbeImage(0));
        Assert.Equal(0x108u, rootSector);
        Assert.Equal(2048u, rootSize);
        Assert.Equal(0, discLseek);
    }

    [Fact]
    public void VerifyXiso_GlobalBaseMagic_ReturnsGlobalLseek()
    {
        var (rootSector, rootSize, discLseek) = Verify(CreateProbeImage(Constants.GlobalLseekOffset));
        Assert.Equal(0x108u, rootSector);
        Assert.Equal(2048u, rootSize);
        Assert.Equal((long)Constants.GlobalLseekOffset, discLseek);
    }

    [Fact]
    public void VerifyXiso_Xgd3BaseMagic_ReturnsXgd3Lseek()
    {
        var (_, _, discLseek) = Verify(CreateProbeImage(Constants.Xgd3LseekOffset));
        Assert.Equal((long)Constants.Xgd3LseekOffset, discLseek);
    }

    [Fact]
    public void VerifyXiso_Xgd1BaseMagic_ReturnsXgd1Lseek()
    {
        var (_, _, discLseek) = Verify(CreateProbeImage(Constants.Xgd1LseekOffset));
        Assert.Equal((long)Constants.Xgd1LseekOffset, discLseek);
    }

    [Fact]
    public void VerifyXiso_BaseZeroWinsWhenBothPresent()
    {
        // Probe order must prefer the plain base, mirroring extract-xiso's chain.
        var path = CreateProbeImage(0);
        var magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(Constants.GlobalLseekOffset + Constants.HeaderOffset + 64);
            fs.Seek(Constants.GlobalLseekOffset + Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(magic, 0, magic.Length);
        }

        var (_, _, discLseek) = Verify(path);
        Assert.Equal(0, discLseek);
    }

    [Fact]
    public void VerifyXiso_NoMagicAnywhere_ThrowsXisoFormatException()
    {
        // Sparse file spanning every probe offset (reads back zeros, no
        // allocation); all probes miss, so the chain must reject the image.
        var path = Path.Combine(Path.GetTempPath(), $"xiso_probe_{Guid.NewGuid():N}.iso");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.SetLength((long)Constants.Xgd2HybridLseekOffset + Constants.HeaderOffset + 64);
        _tempFiles.Add(path);
        Assert.Throws<XisoFormatException>(() => Verify(path));
    }
}
