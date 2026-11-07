using System.Buffers.Binary;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;
using SharpEntry = StfsSharp.Models.StfsEntry;
using SharpGodTitleInfo = StfsSharp.Models.GodTitleInfo;
using SharpTitleInfo = StfsSharp.Models.StfsTitleInfo;
using SharpValidationReport = StfsSharp.Models.StfsValidationReport;
using StfsSharpPackage = StfsSharp.StfsPackage;
using XisoSharpPackage = XISOSharp.StfsPackage;

namespace XISOSharp.Tests;

/// <summary>
/// Verifies that the XISOSharp GOD/STFS facades produce exactly the same
/// results as the standalone StfsSharp package they delegate to.
/// </summary>
[Collection("Sequential")]
public sealed class StfsSharpFacadeTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "xiso_stfs_facade", Guid.NewGuid().ToString("N"));

    public StfsSharpFacadeTests()
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
    public void StfsPackageFacade_MatchesStfsSharp()
    {
        string packagePath = Path.Combine(_work, "game");
        SyntheticStfsPackage.Create(packagePath, [
            new SyntheticStfsFile("default.xex", [1, 2, 3]),
            new SyntheticStfsFile("data/large.bin", CreatePayload(5000)),
            new SyntheticStfsFile("data/empty.bin", []),
        ], extraDirectories: ["empty-dir"]);

        using XisoSharpPackage facade = XisoSharpPackage.Open(packagePath);
        using StfsSharpPackage direct = StfsSharpPackage.Open(packagePath);

        Assert.Equal(direct.Info.TitleName, facade.Info.TitleName);
        Assert.Equal(direct.Info.TitleId, facade.Info.TitleId);
        Assert.Equal(direct.Info.PackageLength, facade.Info.PackageLength);
        Assert.Equal(direct.Info.TotalBlockCount, facade.Info.TotalBlockCount);
        Assert.Equal(
            direct.Entries.Select(static entry => entry.Path).ToArray(),
            facade.Entries.Select(static entry => entry.Path).ToArray(),
            StringComparer.Ordinal);

        StfsEntry facadeEntry = facade.Find("data/large.bin")!;
        SharpEntry directEntry = direct.Find("data/large.bin")!;
        Assert.Equal(directEntry.Size, facadeEntry.Size);
        Assert.Equal(directEntry.BlockCount, facadeEntry.BlockCount);
        Assert.Equal(
            direct.ReadAllBytes(directEntry),
            facade.ReadAllBytes(facadeEntry));

        StfsValidationReport facadeReport = facade.Validate();
        SharpValidationReport directReport = direct.Validate();
        Assert.Equal(directReport.IsValid, facadeReport.IsValid);
        Assert.Equal(directReport.FilesChecked, facadeReport.FilesChecked);
        Assert.Equal(directReport.BlocksChecked, facadeReport.BlocksChecked);
        Assert.Equal(directReport.HashTablesChecked, facadeReport.HashTablesChecked);
        Assert.Equal(directReport.BytesChecked, facadeReport.BytesChecked);
        Assert.Equal(
            directReport.Issues.Select(static issue => (issue.Area, issue.Reason)),
            facadeReport.Issues.Select(static issue => (issue.Area, issue.Reason)));
    }

    [Fact]
    public void StfsHeaderFacade_MatchesStfsSharp()
    {
        string packagePath = Path.Combine(_work, "indie");
        SyntheticStfsPackage.Create(packagePath, [
            new SyntheticStfsFile("default.xex", [1, 2, 3]),
        ], xblig: true, titleName: "Indie Game");

        Assert.True(StfsHeader.TryReadTitle(packagePath, out StfsTitleInfo? facade));
        Assert.True(StfsSharp.StfsHeader.TryReadTitle(packagePath, out SharpTitleInfo? direct));
        Assert.NotNull(facade);
        Assert.NotNull(direct);
        Assert.Equal(direct.TitleName, facade.TitleName);
        Assert.Equal(direct.TitleId, facade.TitleId);
        Assert.Equal(direct.TitleIdHex, facade.TitleIdHex);
        Assert.Equal(direct.ContentType, facade.ContentType);
        Assert.Equal(StfsPackageKind.Xblig, facade.Kind);
    }

    [Fact]
    public void GodFacade_MatchesStfsSharp()
    {
        string header = Path.Combine(_work, "god");
        byte[] headerBytes = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0), 0x4C495645);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x344), 0x7000);
        BinaryPrimitives.WriteUInt32BigEndian(
            headerBytes.AsSpan(GodHeader.TitleIdOffset), 0x4D5307E6);
        headerBytes[GodHeader.DiscNumberOffset] = 1;
        headerBytes[GodHeader.DiscCountOffset] = 1;
        byte[] title = Encoding.BigEndianUnicode.GetBytes("GOD Facade");
        title.AsSpan(0, Math.Min(title.Length, GodHeader.TitleNameLength))
            .CopyTo(headerBytes.AsSpan(GodHeader.TitleNameOffset));
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

        using GodBlockDevice facade = new(header);
        using StfsSharp.GodVolume direct = StfsSharp.GodVolume.Open(header);
        Assert.Equal(direct.Length, facade.Length);
        Assert.Equal(direct.Info.DataFileCount, facade.Info.DataFileCount);
        Assert.Equal(direct.Info.StartingBlock, facade.Info.StartingBlock);
        Assert.Equal(direct.Info.DataBlockCount, facade.Info.DataBlockCount);
        Assert.Equal(direct.Info.EnhancedGdf, facade.Info.EnhancedGdf);
        Assert.Equal(direct.Info.Parts, facade.Info.Parts);

        byte[] facadeSector = new byte[2048];
        byte[] directSector = new byte[2048];
        Assert.Equal(2048, facade.Read(0, facadeSector));
        Assert.Equal(2048, direct.Read(0, directSector));
        Assert.Equal(directSector, facadeSector);
        Assert.Equal(marker, facadeSector);

        Assert.True(GodHeader.TryReadTitle(header, out GodTitleInfo? facadeTitle));
        Assert.True(StfsSharp.GodHeader.TryReadTitle(header, out SharpGodTitleInfo? directTitle));
        Assert.NotNull(facadeTitle);
        Assert.NotNull(directTitle);
        Assert.Equal(directTitle.TitleName, facadeTitle.TitleName);
        Assert.Equal(directTitle.TitleId, facadeTitle.TitleId);
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
