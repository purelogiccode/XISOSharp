using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Phase 10 A5: GOD (SVOD) header title metadata. A GOD container is a header
/// file beside a <c>&lt;header&gt;.data</c> directory; the plaintext XContent
/// header carries the title name, title ID and disc number/count.
/// </summary>
[Collection("Sequential")]
public sealed class GodHeaderTests : IDisposable
{
    private const uint SignatureLive = 0x4C495645;
    private const uint ContentTypeGod = 0x7000;

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void TryReadTitle_ReadsHeaderPathAndDataFolder()
    {
        string work = CreateTempDir("xiso_god");
        (string header, string data) = CreateGodContainer(
            work, "4D5307E6", BuildGodHeader("Halo 3"));

        Assert.True(GodHeader.IsGodContainer(header));
        Assert.True(GodHeader.IsGodContainer(data));
        Assert.True(GodHeader.TryReadTitle(header, out GodTitleInfo? info));
        Assert.NotNull(info);
        Assert.Equal(header, info.HeaderPath);
        Assert.Equal("Halo 3", info.TitleName);
        Assert.Equal(0x4D5307E6u, info.TitleId);
        Assert.Equal("0x4D5307E6", info.TitleIdHex);
        Assert.Equal(1, info.DiscNumber);
        Assert.Equal(2, info.DiscCount);

        Assert.True(GodHeader.TryReadTitle(data, out GodTitleInfo? fromData));
        Assert.Equal(info, fromData);
        Assert.Equal(info, GodHeader.ReadTitle(header));
    }

    [Fact]
    public void TryReadTitle_FallsBackToTitleIdWhenNameEmpty()
    {
        string work = CreateTempDir("xiso_god");
        (string header, _) = CreateGodContainer(
            work, "game", BuildGodHeader(string.Empty, 0x12345678, discNumber: 1, discCount: 1));

        Assert.True(GodHeader.TryReadTitle(header, out GodTitleInfo? info));
        Assert.NotNull(info);
        Assert.Equal("12345678", info.TitleName);
    }

    [Fact]
    public void TryReadTitle_SanitizesFatxUnsafeName()
    {
        string work = CreateTempDir("xiso_god");
        (string header, _) = CreateGodContainer(
            work, "game", BuildGodHeader("Halo: Combat / Evolved\u00AE. "));

        Assert.True(GodHeader.TryReadTitle(header, out GodTitleInfo? info));
        Assert.NotNull(info);
        Assert.Equal("Halo_ Combat _ Evolved", info.TitleName);
    }

    [Fact]
    public void IsGodContainer_RejectsNonGodFiles()
    {
        string work = CreateTempDir("xiso_god");
        string notGod = Path.Combine(work, "notgod");
        File.WriteAllBytes(notGod, new byte[GodHeader.MinimumHeaderLength]);
        Directory.CreateDirectory(notGod + ".data");

        Assert.False(GodHeader.IsGodContainer(notGod));
        Assert.False(GodHeader.TryReadTitle(notGod, out _));
        Assert.Null(GodHeader.ReadTitle(notGod));

        string noData = Path.Combine(work, "nodata");
        File.WriteAllBytes(noData, BuildGodHeader("No Data"));
        Assert.False(GodHeader.IsGodContainer(noData));

        string truncated = Path.Combine(work, "truncated");
        File.WriteAllBytes(truncated, BuildGodHeader("Short")[..0x100]);
        Directory.CreateDirectory(truncated + ".data");
        Assert.False(GodHeader.IsGodContainer(truncated));
        Assert.False(GodHeader.TryReadTitle(truncated, out _));
    }

    [Fact]
    public void CliTitleInfo_ReportsGodMetadata()
    {
        string work = CreateTempDir("xiso_god");
        (string header, string data) = CreateGodContainer(
            work, "4D5307E6", BuildGodHeader("Halo 3"));

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(["title-info", header]));
        Assert.Contains("Title:          Halo 3", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Title ID:       0x4D5307E6", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Disc number:    1", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Disc count:     2", capture.Output, StringComparison.Ordinal);
        Assert.Contains("GOD header:     " + header, capture.Output, StringComparison.Ordinal);

        using LogCapture dataCapture = new();
        Assert.Equal(0, Program.Main(["title-info", data]));
        Assert.Contains("Title:          Halo 3", dataCapture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliTitleInfo_JsonReportsGodMetadata()
    {
        string work = CreateTempDir("xiso_god");
        (string header, _) = CreateGodContainer(
            work, "4D5307E6", BuildGodHeader("Halo 3"));

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(["title-info", "--json", header]));

        using JsonDocument document = JsonDocument.Parse(capture.StdOut);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("title-info", root.GetProperty("command").GetString());
        Assert.Equal("god", root.GetProperty("kind").GetString());
        Assert.Equal("Halo 3", root.GetProperty("titleName").GetString());
        Assert.Equal(0x4D5307E6u, root.GetProperty("titleId").GetUInt32());
        Assert.Equal("0x4D5307E6", root.GetProperty("titleIdHex").GetString());
        Assert.Equal(1, root.GetProperty("discNumber").GetInt32());
        Assert.Equal(2, root.GetProperty("discCount").GetInt32());
        Assert.Equal(header, root.GetProperty("headerPath").GetString());
    }

    [Fact]
    public void CliTitleInfo_NonGodFileFailsCleanly()
    {
        string work = CreateTempDir("xiso_god");
        string notGod = Path.Combine(work, "notgod");
        File.WriteAllBytes(notGod, new byte[GodHeader.MinimumHeaderLength]);

        using LogCapture capture = new();
        Assert.Equal(1, Program.Main(["title-info", notGod]));
        Assert.Contains("Error", capture.Output, StringComparison.Ordinal);
    }

    private static byte[] BuildGodHeader(
        string titleName,
        uint titleId = 0x4D5307E6,
        byte discNumber = 1,
        byte discCount = 2)
    {
        byte[] header = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), SignatureLive);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), ContentTypeGod);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(GodHeader.TitleIdOffset), titleId);
        header[GodHeader.DiscNumberOffset] = discNumber;
        header[GodHeader.DiscCountOffset] = discCount;

        byte[] name = Encoding.BigEndianUnicode.GetBytes(titleName);
        name.AsSpan(0, Math.Min(name.Length, GodHeader.TitleNameLength))
            .CopyTo(header.AsSpan(GodHeader.TitleNameOffset));
        return header;
    }

    private static (string Header, string Data) CreateGodContainer(
        string work, string name, byte[] header)
    {
        string headerPath = Path.Combine(work, name);
        File.WriteAllBytes(headerPath, header);
        string dataPath = headerPath + ".data";
        Directory.CreateDirectory(dataPath);
        return (headerPath, dataPath);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _origOut = Logger.Out;
        private readonly TextWriter _origErr = Logger.Error;
        private readonly bool _origQuiet = Logger.Quiet;
        private readonly bool _origRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        internal LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _err;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        internal string Output => string.Concat(_out, _err);

        internal string StdOut => _out.ToString();

        public void Dispose()
        {
            Logger.Out = _origOut;
            Logger.Error = _origErr;
            Logger.Quiet = _origQuiet;
            Logger.RealQuiet = _origRealQuiet;
            _out.Dispose();
            _err.Dispose();
        }
    }
}
