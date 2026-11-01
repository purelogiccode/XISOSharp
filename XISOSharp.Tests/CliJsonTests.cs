using System.Text.Json;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Contract tests for <c>--json</c> (PlanToEnhance Phase 6): every read-only
/// verb emits one parseable document on stdout with a stable schema, while
/// human progress/status stays on stderr.
/// </summary>
[Collection("Sequential")]
public sealed class CliJsonTests : IDisposable
{
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
    public void Info_Json_EmitsVolumeAndEntries_LogsToStderr()
    {
        string iso = CreateIso();

        (int rc, string stdout, string stderr) = Run("-i", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("info", root.GetProperty("command").GetString());
        Assert.Equal(iso, root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("volume").GetProperty("isValid").GetBoolean());
        Assert.Equal(32, root.GetProperty("volume").GetProperty("descriptorSector").GetInt32());
        Assert.Contains(root.GetProperty("entries").EnumerateArray(),
            static e => string.Equals(e.GetProperty("name").GetString(), "a.txt", StringComparison.Ordinal));

        // Human output must not pollute stdout.
        Assert.DoesNotContain("Volume:", stdout, StringComparison.Ordinal);
        Assert.Contains("Volume:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Json_RecursesAndReportsTotals()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("-l", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("list", root.GetProperty("command").GetString());
        JsonElement image = root.GetProperty("images")[0];
        Assert.Equal(iso, image.GetProperty("source").GetString());
        Assert.Equal(3, image.GetProperty("fileCount").GetInt32());
        Assert.True(image.GetProperty("totalBytes").GetInt64() > 0);
        Assert.Contains(image.GetProperty("entries").EnumerateArray(),
            static e => string.Equals(e.GetProperty("path").GetString(), "/nested/deep.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void List_Json_MultipleInputs_StayInOneDocument()
    {
        string first = CreateIso("first");
        string second = CreateIso("second");

        (int rc, string stdout, _) = Run("-l", "--json", first, second);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public void Tree_Json_UsesTreeCommand()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("-t", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("tree", doc.RootElement.GetProperty("command").GetString());
        Assert.Contains(doc.RootElement.GetProperty("images")[0].GetProperty("entries").EnumerateArray(),
            static e => string.Equals(e.GetProperty("path").GetString(), "/nested/deep.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void List_Json_CciInput_MatchesIsoTree()
    {
        string iso = CreateIso();
        string cci = Path.Combine(Path.GetDirectoryName(iso)!, "game.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, splitBytes: 0));

        (int isoRc, string isoJson, _) = Run("-l", "--json", iso);
        (int cciRc, string cciJson, _) = Run("-l", "--json", cci);

        Assert.Equal(0, isoRc);
        Assert.Equal(0, cciRc);
        using JsonDocument isoDoc = JsonDocument.Parse(isoJson);
        using JsonDocument cciDoc = JsonDocument.Parse(cciJson);
        Assert.Equal(
            isoDoc.RootElement.GetProperty("images")[0].GetProperty("fileCount").GetInt32(),
            cciDoc.RootElement.GetProperty("images")[0].GetProperty("fileCount").GetInt32());
        Assert.Equal(
            isoDoc.RootElement.GetProperty("images")[0].GetProperty("totalBytes").GetInt64(),
            cciDoc.RootElement.GetProperty("images")[0].GetProperty("totalBytes").GetInt64());
    }

    [Fact]
    public void Checksum_Json_MatchesLibraryDigest()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("checksum", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement checksum = doc.RootElement.GetProperty("checksums")[0];
        Assert.Equal("sha3-256", checksum.GetProperty("algorithm").GetString());
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(iso), checksum.GetProperty("hash").GetString());
    }

    [Fact]
    public void SectorLayout_Json_ContainsExtentsAndRanges()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("--sector-layout", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("sector-layout", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("fileCount").GetInt32() >= 3);
        Assert.Contains(root.GetProperty("entries").EnumerateArray(),
            static e => string.Equals(e.GetProperty("path").GetString(), "/a.txt", StringComparison.Ordinal));
        Assert.True(root.GetProperty("usedRanges").GetArrayLength() > 0);
        Assert.True(root.GetProperty("freeRanges").GetArrayLength() > 0);
    }

    [Fact]
    public void Ranges_Json_ContainsSystemAndFileRuns()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("--ranges", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("ranges", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("system").GetArrayLength() > 0);
        Assert.True(root.GetProperty("files").GetArrayLength() > 0);
    }

    [Fact]
    public void Filetime_Json_ReportsIsoAndRawValue()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("--filetime", "--json", iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("filetime", root.GetProperty("command").GetString());
        Assert.Equal(0UL, root.GetProperty("raw").GetUInt64());
        Assert.Equal("0x0000000000000000", root.GetProperty("hex").GetString());
        Assert.StartsWith("1601-01-01", root.GetProperty("iso8601").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Json_ReportsPassedAndSummaries()
    {
        string iso = CreateIso();

        (int rc, string stdout, _) = Run("validate", "--json", iso, iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("validate", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(3, root.GetProperty("sourceSummary").GetProperty("fileCount").GetInt32());
        Assert.Equal(0, root.GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public void Version_Json_ListsComponentsAndCommit()
    {
        (int rc, string stdout, _) = Run("-v", "--json");

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("version", root.GetProperty("command").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("xisoSharp").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("cciSharp").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("zArchiveSharp").GetString()));
        Assert.Contains(".NET", root.GetProperty("runtime").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_WithExtractMode_IsRejected()
    {
        string iso = CreateIso();

        (int rc, string stdout, string stderr) = Run("--json", iso);

        Assert.Equal(1, rc);
        Assert.Empty(stdout.Trim());
        Assert.Contains("--json requires a read-only mode", stderr, StringComparison.Ordinal);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using StringWriter outWriter = new();
        using StringWriter errWriter = new();
        TextWriter savedOut = Console.Out;
        TextWriter savedErr = Console.Error;
        TextWriter savedLoggerOut = Logger.Out;
        TextWriter savedLoggerError = Logger.Error;
        bool savedQuiet = Logger.Quiet;
        bool savedRealQuiet = Logger.RealQuiet;
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            Logger.Out = outWriter;
            Logger.Error = errWriter;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
            int rc = Program.Main(args);
            return (rc, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Logger.Out = savedLoggerOut;
            Logger.Error = savedLoggerError;
            Logger.Quiet = savedQuiet;
            Logger.RealQuiet = savedRealQuiet;
        }
    }

    private string CreateIso(string name = "game")
    {
        string work = Path.Combine(Path.GetTempPath(), $"xiso_cli_json_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        _tempDirs.Add(work);

        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.bin"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "nested"));
        File.WriteAllText(Path.Combine(src, "nested", "deep.txt"), "nested");

        string isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath,
            name + ".iso", null, fileTime: 0));
        Assert.NotNull(isoPath);
        return isoPath;
    }
}
