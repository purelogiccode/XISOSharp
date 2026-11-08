using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoDiscInfo"/> and the <c>disc-info</c> verb.
/// Synthetic Redump images are sparse files: only the volume header, PVD wave
/// timestamp, PFI and security-sector payloads are written.
/// </summary>
[Collection("Sequential")]
public sealed class XisoDiscInfoTests : IDisposable
{
    private readonly List<string> _tempPaths = [];
    private readonly TextWriter _savedOut = Logger.Out;
    private readonly StringWriter _logCapture = new();

    public XisoDiscInfoTests()
    {
        Logger.Out = _logCapture;
    }

    public void Dispose()
    {
        Logger.Out = _savedOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;
        foreach (string path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private string TempFile(string extension = ".iso")
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiso_discinfo_{Guid.NewGuid():N}{extension}");
        _tempPaths.Add(path);
        return path;
    }

    private static void WriteUInt24BigEndian(Span<byte> destination, long value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }

    private static void WriteVolumeHeader(FileStream fs, long partitionOffset, uint rootSector = 0x108,
        uint rootSize = 2048)
    {
        byte[] magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
        fs.Seek(partitionOffset + Constants.HeaderOffset, SeekOrigin.Begin);
        fs.Write(magic);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, rootSector);
        fs.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, rootSize);
        fs.Write(value);
        fs.Seek(Constants.FileTimeSize + Constants.UnusedSize, SeekOrigin.Current);
        fs.Write(magic);
    }

    private static void WritePfi(FileStream fs, long offset, long dataStart, long dataEnd, long layer0End)
    {
        byte[] sector = new byte[Constants.SectorSize];
        sector[0] = 0x01; // DVD-ROM book type, part version 1
        sector[1] = 0x00;
        sector[2] = 0x50; // two layers, opposite track path
        sector[3] = 0x00;
        WriteUInt24BigEndian(sector.AsSpan(5, 3), dataStart);
        WriteUInt24BigEndian(sector.AsSpan(9, 3), dataEnd);
        WriteUInt24BigEndian(sector.AsSpan(13, 3), layer0End);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(sector);
    }

    private static void WriteSecuritySector(FileStream fs, long offset, params (long Start, long End)[] ranges)
    {
        byte[] sector = new byte[Constants.SectorSize];
        sector[1632] = (byte)ranges.Length;
        for (int i = 0; i < ranges.Length; i++)
        {
            int record = 1633 + (i * 9);
            WriteUInt24BigEndian(sector.AsSpan(record + 3, 3), ranges[i].Start);
            WriteUInt24BigEndian(sector.AsSpan(record + 6, 3), ranges[i].End);
        }

        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(sector);
    }

    private static void WriteWave(FileStream fs, string timestamp)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(timestamp);
        fs.Seek(0x832D, SeekOrigin.Begin);
        fs.Write(bytes);
    }

    private string CreateRedumpImage(int redumpType, int xgdType, long gameOffset, long length,
        string? waveTimestamp = null, params (long Start, long End)[] securityRanges)
    {
        string path = TempFile();
        using FileStream fs = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        SparseFile.Prepare(fs, length);
        WriteVolumeHeader(fs, gameOffset);
        if (waveTimestamp != null)
        {
            WriteWave(fs, waveTimestamp);
        }

        WritePfi(fs, gameOffset - (xgdType == 3 ? 0x9800 : 0x1800), 0x30000, length / Constants.SectorSize - 1,
            0x2033AF);
        WriteSecuritySector(fs, gameOffset - (xgdType == 3 ? 0x8800 : 0x800), securityRanges);
        _ = redumpType;
        return path;
    }

    [Fact]
    public void Read_PlainXiso_ReportsRawLayoutAndGamePartition()
    {
        string source = TempFile(".src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        string iso = TempFile();
        _tempPaths.Add(source);
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso));

        XisoDiscLayout info = XisoDiscInfo.Read(iso);

        Assert.Equal(XisoImageFormat.Iso, info.Format);
        Assert.False(info.IsContainer);
        Assert.Equal("RAW", info.XgdType);
        Assert.True(info.Volume.IsValid);
        Assert.Equal(0, info.Volume.DiscLseek);
        Assert.NotNull(info.GamePartition);
        Assert.Equal(0, info.GamePartition.ByteOffset);
        Assert.Null(info.VideoL0);
        Assert.Null(info.VideoL1);
        Assert.Null(info.Pfi);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public void Read_Xgd2RedumpWave3_ReportsVideoPfiAndSecuritySectors()
    {
        const long gameOffset = 0x0FD90000;
        long length = XgdTables.RedumpIsoLength[5];
        string image = CreateRedumpImage(5, 1, gameOffset, length, "2009011416000000",
            (0x100000, 0x100FFF), (0x200000, 0x200FFF));

        XisoDiscLayout info = XisoDiscInfo.Read(image);

        Assert.Equal(5, info.RedumpTypeIndex);
        Assert.True(info.RedumpSizeMatch);
        Assert.Equal("XGD2 Wave 3+", info.RedumpTypeName);
        Assert.Equal("XGD2", info.XgdType);
        Assert.Equal(5, info.VideoTypeIndex);
        Assert.Equal("XGD2 Wave 3", info.VideoTypeName);
        Assert.Equal("XGD2 Wave 3", info.Wave);
        Assert.NotNull(info.VideoL0);
        Assert.Equal(0, info.VideoL0.ByteOffset);
        Assert.Equal(XgdTables.VideoL0Length[5], info.VideoL0.ByteLength);
        Assert.NotNull(info.VideoL1);
        Assert.Equal(length - XgdTables.VideoL1Length[5], info.VideoL1.ByteOffset);
        Assert.NotNull(info.GamePartition);
        Assert.Equal(gameOffset, info.GamePartition.ByteOffset);
        Assert.Equal(XgdTables.XisoLength[1], info.GamePartition.ByteLength);
        Assert.NotNull(info.Pfi);
        Assert.Equal(0x2033AF, info.Pfi.Layer0EndSector);
        Assert.Equal("embedded", info.SecuritySectorsSource);
        Assert.Equal(2, info.SecuritySectors.Count);
        Assert.Equal(0x100000, info.SecuritySectors[0].StartSector);
        Assert.Equal(4096, info.SecuritySectors[0].SectorCount);
        Assert.DoesNotContain(info.Warnings, static warning => warning.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_Xgd3_ReportsXgd3VideoAndPfiAtXgd3Offsets()
    {
        const long gameOffset = 0x02080000;
        long length = XgdTables.RedumpIsoLength[7];
        string image = CreateRedumpImage(7, 3, gameOffset, length, "2010121616000000",
            (0x400000, 0x400FFF), (0x500000, 0x500FFF));

        XisoDiscLayout info = XisoDiscInfo.Read(image);

        Assert.Equal(7, info.RedumpTypeIndex);
        Assert.Equal("XGD3", info.XgdType);
        Assert.Equal(16, info.VideoTypeIndex);
        Assert.Equal("XGD3 Beta", info.VideoTypeName);
        Assert.Equal("XGD3 Beta", info.Wave);
        Assert.NotNull(info.Pfi);
        Assert.Equal(gameOffset - 0x9800, info.Pfi.ByteOffset);
        Assert.Equal(2, info.SecuritySectors.Count);
    }

    [Fact]
    public void Read_Xgd1_ReportsSixteenSecurityRanges()
    {
        const long gameOffset = 0x18300000;
        long length = XgdTables.RedumpIsoLength[1];
        (long Start, long End)[] ranges = Enumerable.Range(0, 16)
            .Select(static i => ((long)(0x100000 + (i * 0x10000)), (long)(0x100000 + (i * 0x10000) + 0xFFF)))
            .ToArray();
        string image = CreateRedumpImage(1, 0, gameOffset, length, null, ranges);

        XisoDiscLayout info = XisoDiscInfo.Read(image);

        Assert.Equal(1, info.RedumpTypeIndex);
        Assert.Equal("XGD1", info.XgdType);
        Assert.Equal(1, info.VideoTypeIndex);
        Assert.Equal("XGD1", info.VideoTypeName);
        Assert.Equal(16, info.SecuritySectors.Count);
    }

    [Fact]
    public void Read_Xgd2Hybrid_ReportsHybridLayout()
    {
        const long gameOffset = 0x89D80000;
        long length = XgdTables.RedumpIsoLength[6];
        string image = CreateRedumpImage(6, 2, gameOffset, length, null, (0x100000, 0x100FFF));

        XisoDiscLayout info = XisoDiscInfo.Read(image);

        Assert.Equal(6, info.RedumpTypeIndex);
        Assert.Equal("XGD2 Hybrid", info.XgdType);
        Assert.Equal(15, info.VideoTypeIndex);
        Assert.Equal("XGD2 Hybrid", info.VideoTypeName);
        Assert.NotNull(info.GamePartition);
        Assert.Equal(gameOffset, info.GamePartition.ByteOffset);
    }

    [Fact]
    public void Read_TruncatedImage_ReportsWarning()
    {
        const long gameOffset = 0x0FD90000;
        long length = XgdTables.RedumpIsoLength[5];
        string image = CreateRedumpImage(5, 1, gameOffset, length, "2009011416000000",
            (0x100000, 0x100FFF));

        using (FileStream fs = new(image, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            SparseFile.Prepare(fs, gameOffset + 0x2000000);
        }

        XisoDiscLayout info = XisoDiscInfo.Read(image);

        Assert.Null(info.RedumpTypeIndex);
        Assert.False(info.RedumpSizeMatch);
        Assert.Contains(info.Warnings, static warning =>
            warning.Contains("not a known Redump image size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_Container_ReportsGamePartitionOnly()
    {
        string source = TempFile(".src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        string iso = TempFile();
        string cso = TempFile(".cso");
        _tempPaths.Add(source);
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso));
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, level: 6));

        XisoDiscLayout info = XisoDiscInfo.Read(cso);

        Assert.Equal(XisoImageFormat.Ciso, info.Format);
        Assert.True(info.IsContainer);
        Assert.NotNull(info.GamePartition);
        Assert.Equal(0, info.GamePartition.ByteOffset);
        Assert.Null(info.VideoL0);
        Assert.Null(info.VideoL1);
        Assert.Null(info.Pfi);
        Assert.Null(info.SecuritySectorsSource);
    }

    [Fact]
    public void Read_ExplicitSectorsTxt_UsedWhenNoEmbeddedSecuritySector()
    {
        string source = TempFile(".src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        string iso = TempFile();
        _tempPaths.Add(source);
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso));
        string sectors = TempFile(".txt");
        File.WriteAllText(sectors, "100-4195\n");

        XisoDiscLayout info = XisoDiscInfo.Read(iso, sectors);

        Assert.Equal("sectors.txt", info.SecuritySectorsSource);
        Assert.Single(info.SecuritySectors);
        Assert.Equal(100, info.SecuritySectors[0].StartSector);
        Assert.Equal(4195, info.SecuritySectors[0].EndSector);
    }

    [Fact]
    public void Cli_DiscInfo_Json_ReportsExpectedFields()
    {
        const long gameOffset = 0x0FD90000;
        long length = XgdTables.RedumpIsoLength[5];
        string image = CreateRedumpImage(5, 1, gameOffset, length, "2009011416000000",
            (0x100000, 0x100FFF), (0x200000, 0x200FFF));

        int exit = Program.Main(["disc-info", image, "--json"]);

        Assert.Equal(0, exit);
        string json = _logCapture.ToString();
        using JsonDocument document = JsonDocument.Parse(json.Trim());
        JsonElement root = document.RootElement;
        Assert.Equal("disc-info", root.GetProperty("command").GetString());
        Assert.Equal("XGD2", root.GetProperty("xgdType").GetString());
        Assert.Equal("XGD2 Wave 3", root.GetProperty("wave").GetString());
        Assert.Equal(5, root.GetProperty("redumpTypeIndex").GetInt32());
        Assert.True(root.GetProperty("redumpSizeMatch").GetBoolean());
        Assert.True(root.GetProperty("volume").GetProperty("isValid").GetBoolean());
        Assert.Equal(gameOffset, root.GetProperty("gamePartition").GetProperty("byteOffset").GetInt64());
        Assert.Equal("embedded", root.GetProperty("securitySectorsSource").GetString());
        Assert.Equal(2, root.GetProperty("securitySectors").GetArrayLength());
        Assert.True(root.GetProperty("pfi").GetProperty("layer0EndSector").GetInt64() == 0x2033AF);
    }

    [Fact]
    public void Cli_DiscInfo_Human_PrintsLayout()
    {
        string source = TempFile(".src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "hello");
        string iso = TempFile();
        _tempPaths.Add(source);
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso));

        int exit = Program.Main(["disc-info", iso]);

        Assert.Equal(0, exit);
        string output = _logCapture.ToString();
        Assert.Contains("XGD type:        RAW", output, StringComparison.Ordinal);
        Assert.Contains("Game partition:", output, StringComparison.Ordinal);
        Assert.Contains("Warnings:        none", output, StringComparison.Ordinal);
    }
}
