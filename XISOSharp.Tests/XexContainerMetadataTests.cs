using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Embedded <c>default.xex</c> metadata for GOD/STFS (BRF-03), XEX thumbnail
/// export (BRF-04) and loose <c>.xbe</c>/<c>.xex</c> inputs (BRF-08).
/// </summary>
[Collection("Sequential")]
public sealed class XexContainerMetadataTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_xex_container_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string PackIso(params (string Name, byte[] Content)[] files)
    {
        string source = TempDir();
        foreach ((string name, byte[] content) in files)
        {
            string path = Path.Combine(source, name);
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(path, content);
        }

        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        return iso;
    }

    private static string RunCli(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedError = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = error;
            int exit = Program.Main(args);
            Assert.Equal(0, exit);
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedError;
        }

        return output.ToString();
    }

    private static JsonDocument RunJson(params string[] args)
    {
        string text = RunCli(args);
        string json = text.Trim();
        int start = json.IndexOf('{', StringComparison.Ordinal);
        Assert.True(start >= 0, $"No JSON object in output: {text}");
        return JsonDocument.Parse(json[start..]);
    }

    [Fact]
    public void LooseXex_ParsesSameHeaderAsImageMember()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf("Loose Title", "Loose Publisher",
            "Loose Developer", "Puzzle", "Loose description");
        string iso = PackIso(("default.xex", xex));
        string looseDir = TempDir();
        string loose = Path.Combine(looseDir, "default.xex");
        File.WriteAllBytes(loose, xex);

        XexInfo? fromImage = XisoReader.GetXexInfo(iso, "/default.xex");
        XexInfo? fromFile = XisoReader.GetLooseXexInfo(loose);

        Assert.NotNull(fromImage);
        Assert.Equal(fromImage, fromFile);
        Assert.Equal(fromImage, XisoReader.GetXexInfo(xex, "default.xex"));

        XexTitleInfo? imageTitle = XisoReader.GetXexTitleInfo(iso, "/default.xex");
        XexTitleInfo? looseTitle = XisoReader.GetLooseXexTitleInfo(loose);
        Assert.NotNull(imageTitle);
        Assert.NotNull(looseTitle);
        Assert.Equal(imageTitle.TitleName, looseTitle.TitleName);
        Assert.Equal(imageTitle.Publisher, looseTitle.Publisher);
        Assert.Equal(imageTitle.Developer, looseTitle.Developer);
        Assert.Equal(imageTitle.Genre, looseTitle.Genre);
        Assert.Equal(imageTitle.Description, looseTitle.Description);
        Assert.Equal(imageTitle.Thumbnail, looseTitle.Thumbnail);
    }

    [Fact]
    public void God_TitleInfoJson_MergesEmbeddedXexMetadata()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf("GOD Embedded", "GOD Publisher",
            "GOD Developer", "Racing", "GOD description");
        string iso = PackIso(("default.xex", xex));
        string header = Path.Combine(TempDir(), "4D5307E6");
        SyntheticGodContainer.CreateFromIso(iso, header, "GOD Header Title");

        using JsonDocument document = RunJson("title-info", header, "--json");
        JsonElement root = document.RootElement;
        Assert.Equal("god", root.GetProperty("kind").GetString());
        Assert.Equal("GOD Header Title", root.GetProperty("titleName").GetString());
        Assert.Equal("/default.xex", root.GetProperty("xexPath").GetString());
        Assert.Equal("GOD Publisher", root.GetProperty("publisher").GetString());
        Assert.Equal("GOD Developer", root.GetProperty("developer").GetString());
        Assert.Equal("Racing", root.GetProperty("genre").GetString());
        Assert.Equal("GOD description", root.GetProperty("description").GetString());
        Assert.True(root.GetProperty("hasThumbnail").GetBoolean());
    }

    [Fact]
    public void God_TitleInfoText_PrintsXexFields()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf("GOD Embedded", "GOD Publisher");
        string iso = PackIso(("default.xex", xex));
        string header = Path.Combine(TempDir(), "4D5307E6");
        SyntheticGodContainer.CreateFromIso(iso, header, "GOD Header Title");

        string text = RunCli("title-info", header);

        Assert.Contains("Title:          GOD Header Title", text, StringComparison.Ordinal);
        Assert.Contains("XEX:            /default.xex", text, StringComparison.Ordinal);
        Assert.Contains("Publisher:      GOD Publisher", text, StringComparison.Ordinal);
        Assert.Contains("Thumbnail:      present", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StfsXblig_TitleInfoJson_MergesEmbeddedXexMetadata()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf("STFS Embedded", "STFS Publisher",
            "STFS Developer", "Arcade", "STFS description");
        string package = Path.Combine(TempDir(), "xblig-package");
        SyntheticStfsPackage.Create(package, [new SyntheticStfsFile("default.xex", xex)],
            xblig: true, titleName: "STFS Header Title");

        using JsonDocument document = RunJson("title-info", package, "--json");
        JsonElement root = document.RootElement;
        Assert.Equal("xblig", root.GetProperty("kind").GetString());
        Assert.Equal("STFS Header Title", root.GetProperty("titleName").GetString());
        Assert.Equal("/default.xex", root.GetProperty("xexPath").GetString());
        Assert.Equal("STFS Publisher", root.GetProperty("publisher").GetString());
        Assert.Equal("STFS Developer", root.GetProperty("developer").GetString());
        Assert.Equal("Arcade", root.GetProperty("genre").GetString());
        Assert.Equal("STFS description", root.GetProperty("description").GetString());
        Assert.True(root.GetProperty("hasThumbnail").GetBoolean());
    }

    [Fact]
    public void StfsXblig_TitleInfoText_PrintsXexFields()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf("STFS Embedded", "STFS Publisher");
        string package = Path.Combine(TempDir(), "xblig-package");
        SyntheticStfsPackage.Create(package, [new SyntheticStfsFile("default.xex", xex)],
            xblig: true, titleName: "STFS Header Title");

        string text = RunCli("title-info", package);

        Assert.Contains("Title:          STFS Header Title", text, StringComparison.Ordinal);
        Assert.Contains("XEX:            /default.xex", text, StringComparison.Ordinal);
        Assert.Contains("Publisher:      STFS Publisher", text, StringComparison.Ordinal);
    }

    [Fact]
    public void XexImage_FromIso_ExportsPngThumbnail()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf();
        string iso = PackIso(("default.xex", xex));
        string output = Path.Combine(TempDir(), "thumb.png");

        RunCli("xex-image", iso, "/default.xex", "--type", "thumbnail", "-o", output, "-y");

        byte[] expected = XisoReader.GetXexTitleInfo(iso, "/default.xex")!.Thumbnail!;
        Assert.Equal(expected, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(output)[..4]);
    }

    [Fact]
    public void XexImage_LooseXex_ExportsPngThumbnail()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf();
        string loose = Path.Combine(TempDir(), "default.xex");
        File.WriteAllBytes(loose, xex);
        string output = Path.Combine(TempDir(), "thumb.png");

        RunCli("xex-image", loose, "--type", "thumbnail", "-o", output, "-y");

        Assert.Equal(XisoReader.GetLooseXexTitleInfo(loose)!.Thumbnail, File.ReadAllBytes(output));
    }

    [Fact]
    public void XexImage_MissingThumbnail_ReportsCleanError()
    {
        byte[] xex = new byte[0x800];
        "XEX2"u8.CopyTo(xex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(8), 0x400);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x10), 0x300);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x14), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x18), 0x00040006);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x1C), 0x200);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x200), 0x2B35C136);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x20C), 0x4D5307D3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x300), 0x1F3C);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(xex.AsSpan(0x410), 0x82000000);
        string loose = Path.Combine(TempDir(), "default.xex");
        File.WriteAllBytes(loose, xex);
        string output = Path.Combine(TempDir(), "thumb.png");

        StringWriter error = new();
        TextWriter savedError = Logger.Error;
        try
        {
            Logger.Error = error;
            int exit = Program.Main(["xex-image", loose, "--type", "thumbnail", "-o", output, "-y"]);
            Assert.Equal(1, exit);
        }
        finally
        {
            Logger.Error = savedError;
        }

        Assert.Contains("no thumbnail resource", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void LooseXbe_InfoJson_MatchesImageMember()
    {
        byte[] xbe = SyntheticXbe.BuildWithImages("Loose XBE", 0x4D530123);
        string iso = PackIso(("default.xbe", xbe));
        string loose = Path.Combine(TempDir(), "default.xbe");
        File.WriteAllBytes(loose, xbe);

        using JsonDocument fromImage = RunJson("--xbe-info", "--json", iso, "/default.xbe");
        using JsonDocument fromFile = RunJson("--xbe-info", "--json", loose);

        Assert.Equal("Loose XBE", fromFile.RootElement.GetProperty("titleName").GetString());
        Assert.Equal(0x4D530123u, fromFile.RootElement.GetProperty("titleId").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("baseAddress").GetUInt32(),
            fromFile.RootElement.GetProperty("baseAddress").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("entryPoint").GetUInt32(),
            fromFile.RootElement.GetProperty("entryPoint").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("sectionCount").GetUInt32(),
            fromFile.RootElement.GetProperty("sectionCount").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("version").GetUInt32(),
            fromFile.RootElement.GetProperty("version").GetUInt32());
        Assert.True(fromFile.RootElement.GetProperty("hasLogoBitmap").GetBoolean());
    }

    [Fact]
    public void LooseXex_InfoJson_MatchesImageMember()
    {
        byte[] xex = SyntheticXex.BuildWithXdbf();
        string iso = PackIso(("default.xex", xex));
        string loose = Path.Combine(TempDir(), "default.xex");
        File.WriteAllBytes(loose, xex);

        using JsonDocument fromImage = RunJson("--xex-info", "--json", iso, "/default.xex");
        using JsonDocument fromFile = RunJson("--xex-info", "--json", loose);

        Assert.Equal("xex-info", fromFile.RootElement.GetProperty("command").GetString());
        Assert.Equal("XDBF Game", fromFile.RootElement.GetProperty("titleName").GetString());
        Assert.Equal(fromImage.RootElement.GetProperty("titleId").GetUInt32(),
            fromFile.RootElement.GetProperty("titleId").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("entryPoint").GetUInt32(),
            fromFile.RootElement.GetProperty("entryPoint").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("moduleFlags").GetUInt32(),
            fromFile.RootElement.GetProperty("moduleFlags").GetUInt32());
        Assert.Equal(fromImage.RootElement.GetProperty("publisher").GetString(),
            fromFile.RootElement.GetProperty("publisher").GetString());
    }

    [Fact]
    public void LooseXbe_Image_ExportsPngLogo()
    {
        byte[] xbe = SyntheticXbe.BuildWithImages("Loose XBE");
        string loose = Path.Combine(TempDir(), "default.xbe");
        File.WriteAllBytes(loose, xbe);
        string output = Path.Combine(TempDir(), "logo.png");

        RunCli("xbe-image", loose, "--type", "logo", "-o", output, "-y");

        byte[] png = File.ReadAllBytes(output);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }
}
