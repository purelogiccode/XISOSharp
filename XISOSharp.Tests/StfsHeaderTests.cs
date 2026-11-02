using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Read-only title metadata from single-file XBLA/XBLIG STFS packages. These
/// packages have no <c>.data</c> directory; the package file itself starts
/// with the same XContent header used by GOD containers.
/// </summary>
[Collection("Sequential")]
public sealed class StfsHeaderTests : IDisposable
{
    private const uint SignatureLive = 0x4C495645;
    private const uint ContentTypeXbla = 0x000D0000;
    private const uint ContentTypeXblig = 0x00000002;

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
    public void TryReadTitle_ReadsXblaAndXbligPackages()
    {
        string work = CreateTempDir("xiso_stfs");
        string xbla = CreatePackage(work, "xbla", BuildHeader(
            "0 day Attack on Earth", ContentTypeXbla, 0x584109B7));
        string xblig = CreatePackage(work, "xblig", BuildHeader(
            "0 Gravity Y3030", ContentTypeXblig, 0x584E07D2));

        Assert.True(StfsHeader.IsStfsPackage(xbla));
        Assert.True(StfsHeader.TryReadTitle(xbla, out StfsTitleInfo? xblaInfo));
        Assert.NotNull(xblaInfo);
        Assert.Equal(Path.GetFullPath(xbla), xblaInfo.PackagePath);
        Assert.Equal(StfsPackageKind.Xbla, xblaInfo.Kind);
        Assert.Equal("0 day Attack on Earth", xblaInfo.TitleName);
        Assert.Equal(0x584109B7u, xblaInfo.TitleId);
        Assert.Equal("0x584109B7", xblaInfo.TitleIdHex);
        Assert.Equal(ContentTypeXbla, xblaInfo.ContentType);
        Assert.Equal(0, xblaInfo.DiscNumber);
        Assert.Equal(0, xblaInfo.DiscCount);
        Assert.Equal(xblaInfo, StfsHeader.ReadTitle(xbla));

        Assert.True(StfsHeader.TryReadTitle(xblig, out StfsTitleInfo? xbligInfo));
        Assert.NotNull(xbligInfo);
        Assert.Equal(StfsPackageKind.Xblig, xbligInfo.Kind);
        Assert.Equal("0 Gravity Y3030", xbligInfo.TitleName);
        Assert.Equal(0x584E07D2u, xbligInfo.TitleId);
        Assert.Equal(ContentTypeXblig, xbligInfo.ContentType);

        // XBLA/XBLIG packages are not GOD containers.
        Assert.False(GodHeader.IsGodContainer(xbla));
        Assert.False(GodHeader.TryReadTitle(xbla, out _));
    }

    [Fact]
    public void TryReadTitle_FallsBackToTitleIdWhenNameEmpty()
    {
        string work = CreateTempDir("xiso_stfs");
        string package = CreatePackage(work, "empty", BuildHeader(
            string.Empty, ContentTypeXbla, 0x12345678));

        Assert.True(StfsHeader.TryReadTitle(package, out StfsTitleInfo? info));
        Assert.NotNull(info);
        Assert.Equal("12345678", info.TitleName);
    }

    [Fact]
    public void TryReadTitle_RejectsGodAndUnknownContentTypes()
    {
        string work = CreateTempDir("xiso_stfs");
        string god = CreatePackage(work, "god", BuildHeader(
            "GOD Title", 0x7000, 0x4D5307E6));
        string unknown = CreatePackage(work, "unknown", BuildHeader(
            "Unknown", 0x00000001, 0x4D5307E6));
        string notXContent = Path.Combine(work, "notxcontent");
        File.WriteAllBytes(notXContent, new byte[GodHeader.MinimumHeaderLength]);

        Assert.False(StfsHeader.IsStfsPackage(god));
        Assert.False(StfsHeader.TryReadTitle(god, out _));
        Assert.False(StfsHeader.IsStfsPackage(unknown));
        Assert.False(StfsHeader.TryReadTitle(unknown, out _));
        Assert.False(StfsHeader.IsStfsPackage(notXContent));
        Assert.False(StfsHeader.TryReadTitle(notXContent, out _));
    }

    [Fact]
    public void CliTitleInfo_ReportsXblaMetadata()
    {
        string work = CreateTempDir("xiso_stfs");
        string package = CreatePackage(work, "xbla", BuildHeader(
            "0 day Attack on Earth", ContentTypeXbla, 0x584109B7));

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(["title-info", package]));
        Assert.Contains("Title:          0 day Attack on Earth", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Title ID:       0x584109B7", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Package:        XBLA", capture.Output, StringComparison.Ordinal);
        Assert.Contains("Content type:   0x000D0000", capture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CliTitleInfo_JsonReportsXbligMetadata()
    {
        string work = CreateTempDir("xiso_stfs");
        string package = CreatePackage(work, "xblig", BuildHeader(
            "0 Gravity Y3030", ContentTypeXblig, 0x584E07D2));

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(["title-info", "--json", package]));

        using JsonDocument document = JsonDocument.Parse(capture.StdOut);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("title-info", root.GetProperty("command").GetString());
        Assert.Equal("xblig", root.GetProperty("kind").GetString());
        Assert.Equal("0 Gravity Y3030", root.GetProperty("titleName").GetString());
        Assert.Equal(0x584E07D2u, root.GetProperty("titleId").GetUInt32());
        Assert.Equal("0x584E07D2", root.GetProperty("titleIdHex").GetString());
        Assert.Equal(ContentTypeXblig, root.GetProperty("contentType").GetUInt32());
        Assert.Equal(Path.GetFullPath(package), root.GetProperty("packagePath").GetString());
    }

    private static byte[] BuildHeader(
        string titleName,
        uint contentType,
        uint titleId,
        byte discNumber = 0,
        byte discCount = 0)
    {
        byte[] header = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), SignatureLive);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x344), contentType);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(GodHeader.TitleIdOffset), titleId);
        header[GodHeader.DiscNumberOffset] = discNumber;
        header[GodHeader.DiscCountOffset] = discCount;

        byte[] name = Encoding.BigEndianUnicode.GetBytes(titleName);
        name.AsSpan(0, Math.Min(name.Length, GodHeader.TitleNameLength))
            .CopyTo(header.AsSpan(GodHeader.TitleNameOffset));
        return header;
    }

    private static string CreatePackage(string work, string name, byte[] header)
    {
        string path = Path.Combine(work, name);
        File.WriteAllBytes(path, header);
        return path;
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
