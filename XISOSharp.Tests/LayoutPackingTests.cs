using System.Security.Cryptography;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for explicit sector-layout packing (PlanToEnhance Phase 8):
/// <see cref="XisoLayoutSpec"/> parsing (JSON from <c>--sector-layout --json</c>
/// and the compact <c>path=sector</c> text form), fixed-extent placement,
/// first-fit allocation around fixed extents, validation failures, and the
/// CLI/create/build-image wiring.
/// </summary>
[Collection("Sequential")]
public class LayoutPackingTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    public LayoutPackingTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedLoggerOut = Logger.Out;
        _savedLoggerError = Logger.Error;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
    }

    public void Dispose()
    {
        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedLoggerOut;
        Logger.Error = _savedLoggerError;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _outCapture.Dispose();
        _errCapture.Dispose();
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

        GC.SuppressFinalize(this);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateSource(string name = "layout_src")
    {
        string src = CreateTempDir(name);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello layout");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[5002]);
        return src;
    }

    private string Pack(string src, XisoLayoutSpec? layout = null, ulong? fileTime = 0,
        string outputName = "packed.iso")
    {
        string outDir = CreateTempDir("layout_out");
        string iso = Path.Combine(outDir, outputName);
        int rc = XisoWriter.PackFromDirectory(src, iso, fileTime: fileTime, layout: layout);
        Assert.Equal(0, rc);
        Assert.True(File.Exists(iso));
        return iso;
    }

    private string WriteLayoutText(string content)
    {
        string path = Path.Combine(CreateTempDir("layout_spec"), "layout.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteLayoutJson(string content)
    {
        string path = Path.Combine(CreateTempDir("layout_spec"), "layout.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static FileSectorExtent Entry(SectorLayout layout, string path) =>
        layout.Entries.Single(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ParseText_ReadsCommentsHexAndTotalSectors()
    {
        XisoLayoutSpec spec = XisoLayoutSpec.ParseText(
            "# fixed extents\n" +
            "; another comment\n" +
            "total-sectors=512\n" +
            "readme.txt=0x190\n" +
            "/sub/game.bin = 300\n");

        Assert.Equal(512, spec.TotalSectors);
        Assert.True(spec.StrictPaths);
        Assert.Equal(2, spec.Entries.Count);
        Assert.Equal("/readme.txt", spec.Entries[0].Path);
        Assert.Equal(400u, spec.Entries[0].StartSector);
        Assert.Equal("/sub/game.bin", spec.Entries[1].Path);
        Assert.Equal(300u, spec.Entries[1].StartSector);
        Assert.Null(spec.Entries[1].SectorCount);
    }

    [Theory]
    [InlineData("/a=1\n/a=2")]
    [InlineData("/a=nope")]
    [InlineData("/a")]
    [InlineData("total-sectors=0")]
    [InlineData("/../a=10")]
    public void ParseText_InvalidContent_Throws(string text) =>
        Assert.Throws<FormatException>(() => XisoLayoutSpec.ParseText(text));

    [Fact]
    public void ParseJson_ReadsSectorLayoutPayload()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "command": "sector-layout",
          "volume": { "totalSectors": 512 },
          "entries": [
            { "path": "/", "isDirectory": true, "startSector": 264, "sectorCount": 1, "size": 40 },
            { "path": "/a.txt", "isDirectory": false, "startSector": 400, "sectorCount": 1, "size": 14 }
          ]
        }
        """;

        XisoLayoutSpec spec = XisoLayoutSpec.ParseJson(json);

        Assert.Equal(512, spec.TotalSectors);
        Assert.False(spec.StrictPaths);
        Assert.Equal(2, spec.Entries.Count);
        Assert.Equal(264u, spec.Entries[0].StartSector);
        Assert.True(spec.Entries[0].IsDirectory);
        Assert.Equal(1u, spec.Entries[1].SectorCount);
        Assert.Equal(14u, spec.Entries[1].FileSize);
    }

    [Fact]
    public void ParseJson_AcceptsPascalCaseAliasesAndRootArray()
    {
        const string json = """
        [
          { "Path": "/a.bin", "Sector": 300, "Size": 2049 }
        ]
        """;

        XisoLayoutSpec spec = XisoLayoutSpec.ParseJson(json);

        XisoLayoutEntry entry = Assert.Single(spec.Entries);
        Assert.Equal("/a.bin", entry.Path);
        Assert.Equal(300u, entry.StartSector);
        Assert.Equal(2u, entry.SectorCount);
        Assert.Equal(2049u, entry.FileSize);
        Assert.Null(spec.TotalSectors);
    }

    [Theory]
    [InlineData("""{"entries":[{"path":"/a","startSector":1}]}""")]
    [InlineData("""{"entries":[{"path":"/a","startSector":1,"sectorCount":1},{"path":"/A","startSector":2,"sectorCount":1}]}""")]
    [InlineData("""{"entries":[{"path":"/a","startSector":-1,"sectorCount":1}]}""")]
    [InlineData("""{"entries":"nope"}""")]
    [InlineData("""{"volume":{"totalSectors":0},"entries":[]}""")]
    [InlineData("not json")]
    public void ParseJson_InvalidContent_Throws(string json) =>
        Assert.Throws<FormatException>(() => XisoLayoutSpec.ParseJson(json));

    [Fact]
    public void FromSectorLayout_CopiesEveryExtent()
    {
        string iso = Pack(CreateSource());
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        XisoLayoutSpec spec = XisoLayoutSpec.FromSectorLayout(layout);

        Assert.Equal(layout.TotalSectors, spec.TotalSectors);
        Assert.Equal(layout.Entries.Count, spec.Entries.Count);
        Assert.False(spec.StrictPaths);
        foreach (FileSectorExtent extent in layout.Entries)
        {
            XisoLayoutEntry entry = spec.Entries.Single(e =>
                string.Equals(e.Path, extent.Path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(extent.StartSector, entry.StartSector);
            Assert.Equal(extent.SectorCount, entry.SectorCount);
            Assert.Equal(extent.FileSize, entry.FileSize);
            Assert.Equal(extent.IsDirectory, entry.IsDirectory);
        }
    }

    [Fact]
    public void Parse_StripsUtf8Bom()
    {
        XisoLayoutSpec text = XisoLayoutSpec.Parse("\uFEFF/a=300\n");
        Assert.Equal("/a", Assert.Single(text.Entries).Path);

        XisoLayoutSpec json = XisoLayoutSpec.Parse(
            "\uFEFF{\"entries\":[{\"path\":\"/a\",\"startSector\":300,\"sectorCount\":1}]}");
        Assert.Single(json.Entries);
    }

    [Fact]
    public void Load_DetectsJsonAndText()
    {
        string json = WriteLayoutJson("""{"entries":[{"path":"/a","startSector":300,"sectorCount":1}]}""");
        string text = WriteLayoutText("/a=300\n");

        Assert.Single(XisoLayoutSpec.Load(json).Entries);
        Assert.Single(XisoLayoutSpec.Load(text).Entries);
    }

    [Fact]
    public void Load_MissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() =>
            XisoLayoutSpec.Load(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json")));

    [Fact]
    public void PackWithJsonLayout_RoundTripsByteIdentical()
    {
        string src = CreateSource();
        string first = Pack(src, outputName: "first.iso");
        SectorLayout layout = XisoReader.GetSectorLayout(first);

        string second = Pack(src, XisoLayoutSpec.FromSectorLayout(layout), outputName: "second.iso");

        Assert.Equal(SHA256.HashData(File.ReadAllBytes(first)),
            SHA256.HashData(File.ReadAllBytes(second)));
        SectorLayout secondLayout = XisoReader.GetSectorLayout(second);
        Assert.Equal(layout.TotalSectors, secondLayout.TotalSectors);
        Assert.Equal(
            layout.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)
                .Select(e => (e.Path, e.StartSector, e.SectorCount)),
            secondLayout.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)
                .Select(e => (e.Path, e.StartSector, e.SectorCount)));
    }

    [Fact]
    public void PackWithJsonLayout_ManyEntries_RoundTripsByteIdentical()
    {
        string src = CreateTempDir("layout_many_src");
        for (int d = 0; d < 6; d++)
        {
            string dir = Path.Combine(src, $"dir{d}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 20; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"file{f}.txt"), $"dir{d}/file{f}");
            }
        }

        string first = Pack(src, outputName: "many1.iso");
        SectorLayout layout = XisoReader.GetSectorLayout(first);
        Assert.True(layout.Entries.Count > 100);

        string second = Pack(src, XisoLayoutSpec.FromSectorLayout(layout), outputName: "many2.iso");

        Assert.Equal(SHA256.HashData(File.ReadAllBytes(first)),
            SHA256.HashData(File.ReadAllBytes(second)));
    }

    [Fact]
    public void PackWithTextLayout_PlacesFixedEntryAndAllocatesFirstFit()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("total-sectors=512\n/a.txt=400\n");

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(512, layout.TotalSectors);
        Assert.Equal(400u, Entry(layout, "/a.txt").StartSector);
        Assert.Equal(264u, Entry(layout, "/").StartSector);
        // /sub and /sub/b.bin are not in the layout: first-fit after the root table.
        Assert.Equal(265u, Entry(layout, "/sub").StartSector);
        Assert.Equal(266u, Entry(layout, "/sub/b.bin").StartSector);
    }

    [Fact]
    public void PackWithLayout_PreservesDeclaredTotalSectors()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("total-sectors=512\n/a.txt=400\n");

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));

        Assert.Equal(512, XisoReader.GetSectorLayout(iso).TotalSectors);
    }

    [Fact]
    public void PackWithLayout_FixedRootMovesDynamicAllocations()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("/=300\n/a.txt=400\n");

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(300u, Entry(layout, "/").StartSector);
        Assert.Equal(400u, Entry(layout, "/a.txt").StartSector);
        // Dynamic allocations start at the standard root sector and fill the gap.
        Assert.Equal(264u, Entry(layout, "/sub").StartSector);
        AuditResult audit = XisoReader.AuditXiso(iso);
        Assert.True(audit.IsValid, string.Join("; ", audit.Issues));
    }

    [Fact]
    public void PackWithJsonLayout_ReservesUnmatchedExtents()
    {
        string src = CreateSource();
        const string json = """
        {
          "volume": { "totalSectors": 608 },
          "entries": [
            { "path": "/", "isDirectory": true, "startSector": 500, "sectorCount": 1, "size": 40 },
            { "path": "/missing.bin", "isDirectory": false, "startSector": 264, "sectorCount": 1, "size": 1 }
          ]
        }
        """;
        string specPath = WriteLayoutJson(json);

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(500u, Entry(layout, "/").StartSector);
        // The unmatched /missing.bin extent at 264 is reserved, so the first
        // dynamic file starts at 265 instead of reusing it.
        Assert.Equal(265u, Entry(layout, "/a.txt").StartSector);
        Assert.Equal(608, layout.TotalSectors);
    }

    [Fact]
    public void PackWithLayout_TooSmallExtent_Throws()
    {
        string src = CreateSource();
        const string json = """
        {
          "entries": [
            { "path": "/sub/b.bin", "isDirectory": false, "startSector": 400, "sectorCount": 1, "size": 5002 }
          ]
        }
        """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Pack(src, XisoLayoutSpec.ParseJson(json)));
        Assert.Contains("/sub/b.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("needs 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackWithLayout_Overlap_Throws()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("/a.txt=300\n/sub/b.bin=300\n");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Pack(src, XisoLayoutSpec.Load(specPath)));
        Assert.Contains("overlaps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackWithLayout_OutOfRange_Throws()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("total-sectors=300\n/sub/b.bin=299\n");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Pack(src, XisoLayoutSpec.Load(specPath)));
        Assert.Contains("out of range", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/a.txt=32")]
    [InlineData("/a.txt=16")]
    [InlineData("/a.txt=0")]
    public void PackWithLayout_SystemArea_Throws(string line)
    {
        string src = CreateSource();
        string specPath = WriteLayoutText(line + "\n");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Pack(src, XisoLayoutSpec.Load(specPath)));
        Assert.Contains("system area", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackWithLayout_UnknownTextPath_Throws()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("/does-not-exist.bin=300\n");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Pack(src, XisoLayoutSpec.Load(specPath)));
        Assert.Contains("not found in the source tree", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackWithLayout_PrependSectors_KeepsLayoutRelative()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("/a.txt=400\n");
        string outDir = CreateTempDir("layout_out");
        string iso = Path.Combine(outDir, "prepended.iso");

        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out _, "prepended.iso", null,
            prependSectors: 10, fileTime: 0, layout: XisoLayoutSpec.Load(specPath)));

        SectorLayout layout = XisoReader.GetSectorLayout(iso, skipSectors: 10);
        Assert.Equal(400u, Entry(layout, "/a.txt").StartSector);
        Assert.Equal(10L * Constants.SectorSize, XisoReader.GetVolumeInfo(iso, 10).DiscLseek);
    }

    [Fact]
    public void BuildImage_Layout_PlacesFixedEntry()
    {
        string src = CreateSource();
        string specPath = WriteLayoutText("/a.txt=450\n/sub/b.bin=460\n");
        string outDir = CreateTempDir("layout_out");
        string iso = Path.Combine(outDir, "remap.iso");
        Assert.True(RemapRule.TryParse("**:{0}", out RemapRule? rule, out _));

        int rc = RemapFilesystem.BuildImage(src, iso, [rule!], fileTime: 0,
            layout: XisoLayoutSpec.Load(specPath));

        Assert.Equal(0, rc);
        SectorLayout layout = XisoReader.GetSectorLayout(iso);
        Assert.Equal(450u, Entry(layout, "/a.txt").StartSector);
        Assert.Equal(460u, Entry(layout, "/sub/b.bin").StartSector);
    }

    [Fact]
    public void Cli_Create_WithLayout_EndToEnd()
    {
        string src = CreateSource();
        string outDir = CreateTempDir("layout_cli_out");
        string iso = Path.Combine(outDir, "cli.iso");
        string specPath = WriteLayoutText("total-sectors=512\n/a.txt=400\n");

        Assert.Equal(0, Program.Main(["-c", src, iso, "--file-time", "0", "--layout", specPath]));

        SectorLayout layout = XisoReader.GetSectorLayout(iso);
        Assert.Equal(400u, Entry(layout, "/a.txt").StartSector);
        Assert.Equal(512, layout.TotalSectors);
    }

    [Fact]
    public void Cli_Pack_WithLayout_EndToEnd()
    {
        string src = CreateSource();
        string outDir = CreateTempDir("layout_cli_out");
        string iso = Path.Combine(outDir, "packed.iso");
        string specPath = WriteLayoutText("/a.txt=400\n");

        Assert.Equal(0, Program.Main(["--pack", src, iso, "--file-time", "0", "--layout", specPath]));

        Assert.Equal(400u, Entry(XisoReader.GetSectorLayout(iso), "/a.txt").StartSector);
    }

    [Fact]
    public void Cli_BuildImage_WithLayout_EndToEnd()
    {
        string src = CreateSource();
        string outDir = CreateTempDir("layout_cli_out");
        string iso = Path.Combine(outDir, "built.iso");
        string specPath = WriteLayoutText("/a.txt=450\n");

        Assert.Equal(0, Program.Main(
            ["build-image", "-m", "**:{0}", src, iso, "--file-time", "0", "--layout", specPath]));

        Assert.Equal(450u, Entry(XisoReader.GetSectorLayout(iso), "/a.txt").StartSector);
    }

    [Fact]
    public void Cli_SectorLayoutJson_RoundTripsThroughLayout()
    {
        string src = CreateSource();
        string first = Pack(src, outputName: "first.iso");
        _outCapture.GetStringBuilder().Clear();

        Assert.Equal(0, Program.Main(["--sector-layout", "--json", first]));
        string json = _outCapture.ToString();
        Assert.Contains("\"entries\"", json, StringComparison.Ordinal);
        string specPath = WriteLayoutJson(json);

        string outDir = CreateTempDir("layout_cli_out");
        string second = Path.Combine(outDir, "second.iso");
        Assert.Equal(0, Program.Main(["-c", src, second, "--file-time", "0", "--layout", specPath]));

        Assert.Equal(SHA256.HashData(File.ReadAllBytes(first)),
            SHA256.HashData(File.ReadAllBytes(second)));
    }

    [Fact]
    public void Cli_Layout_UsageErrors()
    {
        string src = CreateSource();
        string outDir = CreateTempDir("layout_cli_out");
        string iso = Path.Combine(outDir, "out.iso");
        string specPath = WriteLayoutText("/a.txt=400\n");
        string missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json");

        Assert.Equal(1, Program.Main(["-c", src, iso, "--layout"]));
        Assert.Equal(1, Program.Main(["-l", iso, "--layout", specPath]));
        Assert.Equal(1, Program.Main(["--layout", specPath]));
        Assert.Equal(1, Program.Main(["-c", src, iso, "--layout", missing]));
        // A rewrite (--pack <iso>) has no host files to place.
        Assert.Equal(1, Program.Main(["--pack", iso, "--layout", specPath]));
        Assert.Equal(1, Program.Main(["-r", Pack(src, outputName: "rewrite-src.iso"), "--layout", specPath]));
        Assert.Equal(1, Program.Main(["build-image", "--layout"]));
    }

    [Fact]
    public void Cli_Layout_MisplacedAfterFilename_IsRefused()
    {
        string src = CreateSource();
        string outDir = CreateTempDir("layout_cli_out");
        string iso = Path.Combine(outDir, "out.iso");
        string specPath = WriteLayoutText("/a.txt=400\n");

        Assert.Equal(1, Program.Main(["-c", src, iso, "--file-time", "0", iso, "--layout", specPath]));
        Assert.Contains("--layout", _errCapture.ToString(), StringComparison.Ordinal);
        Assert.Contains("must come before", _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PackWithLayout_EmptyDirectoryAndEmptyFile_ArePlaced()
    {
        string src = CreateTempDir("layout_src");
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        File.WriteAllBytes(Path.Combine(src, "empty.bin"), []);
        File.WriteAllText(Path.Combine(src, "a.txt"), "a");
        string specPath = WriteLayoutText("/empty=300\n/empty.bin=350\n");

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));
        SectorLayout layout = XisoReader.GetSectorLayout(iso);

        Assert.Equal(300u, Entry(layout, "/empty").StartSector);
        Assert.Equal(350u, Entry(layout, "/empty.bin").StartSector);
        Assert.Equal(0u, Entry(layout, "/empty.bin").SectorCount);
    }

    [Fact]
    public void PackWithLayout_CaseInsensitivePaths()
    {
        string src = CreateTempDir("layout_src");
        File.WriteAllText(Path.Combine(src, "A.txt"), "a");
        string specPath = WriteLayoutText("/a.TXT=400\n");

        string iso = Pack(src, XisoLayoutSpec.Load(specPath));

        Assert.Equal(400u, Entry(XisoReader.GetSectorLayout(iso), "/A.txt").StartSector);
    }
}
