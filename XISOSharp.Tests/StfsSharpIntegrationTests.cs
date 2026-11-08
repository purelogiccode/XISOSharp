using System.Buffers.Binary;
using System.Text;
using XISOSharp.Interfaces;
using XISOSharp.TestDataGenerator;
using StfsEntry = StfsSharp.Models.StfsEntry;
using StfsPackage = StfsSharp.StfsPackage;
using StfsTitleInfo = StfsSharp.Models.StfsTitleInfo;
using StfsValidationReport = StfsSharp.Models.StfsValidationReport;

namespace XISOSharp.Tests;

/// <summary>
/// XISOSharp consumes the standalone StfsSharp package directly: STFS package
/// reads/validation, XBLA/XBLIG title metadata and GOD/SVOD sector mapping all
/// go through the StfsSharp types rather than XISOSharp facades.
/// </summary>
[Collection("Sequential")]
public sealed class StfsSharpIntegrationTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "xiso_stfs_integration", Guid.NewGuid().ToString("N"));

    public StfsSharpIntegrationTests()
    {
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void StfsPackage_ReadsAndValidatesDirectly()
    {
        string packagePath = Path.Combine(_work, "game");
        SyntheticStfsPackage.Create(packagePath, [
            new SyntheticStfsFile("default.xex", [1, 2, 3]),
            new SyntheticStfsFile("data/large.bin", CreatePayload(5000)),
            new SyntheticStfsFile("data/empty.bin", []),
        ], extraDirectories: ["empty-dir"]);

        using StfsPackage package = StfsPackage.Open(packagePath);

        Assert.Equal("Synthetic STFS", package.Info.TitleName);
        Assert.Equal(0x584109B7u, package.Info.TitleId);
        Assert.True(package.Info.PackageLength > 0);
        Assert.Contains(package.Entries,
            static entry => string.Equals(entry.Path, "data/large.bin", StringComparison.Ordinal));

        StfsEntry large = Assert.IsType<StfsEntry>(package.Find("data/large.bin"));
        Assert.Equal(5000, large.Size);
        Assert.Equal(CreatePayload(5000), package.ReadAllBytes(large));

        StfsValidationReport report = package.Validate();
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(static issue => issue.Reason)));
    }

    [Fact]
    public void StfsHeader_ReadsTitleDirectly()
    {
        string packagePath = Path.Combine(_work, "indie");
        SyntheticStfsPackage.Create(packagePath, [
            new SyntheticStfsFile("default.xex", [1, 2, 3]),
        ], xblig: true, titleName: "Indie Game");

        Assert.True(StfsSharp.StfsHeader.TryReadTitle(packagePath, out StfsTitleInfo? title));
        Assert.NotNull(title);
        Assert.Equal("Indie Game", title.TitleName);
        Assert.Equal(StfsSharp.Models.StfsPackageKind.Xblig, title.Kind);
    }

    [Fact]
    public void GodVolume_IsExposedThroughXisoImageSource()
    {
        string header = Path.Combine(_work, "god");
        byte[] headerBytes = new byte[StfsSharp.GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0), 0x4C495645);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x344), 0x7000);
        BinaryPrimitives.WriteUInt32BigEndian(
            headerBytes.AsSpan(StfsSharp.GodHeader.TitleIdOffset), 0x4D5307E6);
        headerBytes[StfsSharp.GodHeader.DiscNumberOffset] = 1;
        headerBytes[StfsSharp.GodHeader.DiscCountOffset] = 1;
        byte[] title = Encoding.BigEndianUnicode.GetBytes("GOD Integration");
        title.AsSpan(0, Math.Min(title.Length, StfsSharp.GodHeader.TitleNameLength))
            .CopyTo(headerBytes.AsSpan(StfsSharp.GodHeader.TitleNameOffset));
        WriteUInt24LittleEndian(headerBytes, 0x392, 0x22);
        WriteUInt24LittleEndian(headerBytes, 0x395, 0);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x39D), 1);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x3A9), 1);
        File.WriteAllBytes(header, headerBytes);

        string dataDirectory = header + ".data";
        Directory.CreateDirectory(dataDirectory);
        byte[] marker = CreatePayload(2048);
        string dataPath = Path.Combine(dataDirectory, "Data0000");
        using (FileStream data = new(dataPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            data.SetLength(0x24000);
            data.Seek(0x2000, SeekOrigin.Begin);
            data.Write(marker);
        }

        using StfsSharp.GodVolume volume = StfsSharp.GodVolume.Open(header);
        Assert.Equal(1u, volume.Info.DataFileCount);

        using IBlockDevice device = XisoImageSource.OpenBlockDevice(header);
        byte[] directSector = new byte[2048];
        byte[] sourceSector = new byte[2048];
        Assert.Equal(2048, volume.Read(0, directSector));
        Assert.Equal(2048, device.Read(0, sourceSector));
        Assert.Equal(directSector, sourceSector);
        Assert.Equal(marker, sourceSector);

        Assert.True(StfsSharp.GodHeader.TryReadTitle(header, out StfsSharp.Models.GodTitleInfo? titleInfo));
        Assert.NotNull(titleInfo);
        Assert.Equal("GOD Integration", titleInfo.TitleName);
        Assert.Equal(0x4D5307E6u, titleInfo.TitleId);
    }

    private static byte[] CreatePayload(int length)
    {
        byte[] data = new byte[length];
        for (int index = 0; index < length; index++)
        {
            data[index] = (byte)((index * 31) + 7);
        }

        return data;
    }

    private static void WriteUInt24LittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }
}
