using System.Text;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// BRF-09: Redump DAT verification. The tests build synthetic DATs from the
/// actual image hashes, so no external DAT source or game data is required.
/// </summary>
[Collection("Sequential")]
public sealed class XisoDatTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly bool _savedQuiet;
    private readonly bool _savedRealQuiet;
    private readonly bool _savedRouteInfoToError;

    public XisoDatTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedQuiet = Logger.Quiet;
        _savedRealQuiet = Logger.RealQuiet;
        _savedRouteInfoToError = Logger.RouteInfoToError;
        Console.SetOut(_out);
        Console.SetError(_err);
        Logger.Out = _out;
        Logger.Error = _err;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;
    }

    public void Dispose()
    {
        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedOut;
        Logger.Error = _savedErr;
        Logger.Quiet = _savedQuiet;
        Logger.RealQuiet = _savedRealQuiet;
        Logger.RouteInfoToError = _savedRouteInfoToError;
        _out.Dispose();
        _err.Dispose();
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void Verify_MatchesSyntheticDatAndReportsMissingEntries()
    {
        string work = CreateTempDir("xiso_dat");
        string isoA = CreateIso(work, "alpha");
        string isoB = CreateIso(work, "beta");
        XisoImageHashResult hashA = XisoImageHash.Compute(isoA);
        XisoImageHashResult hashB = XisoImageHash.Compute(isoB);

        string dat = WriteDat(work, "games.dat",
            ("Alpha Game", Path.GetFileName(isoA), hashA),
            ("Beta Game", Path.GetFileName(isoB), hashB),
            ("Never Supplied", "missing.iso", hashA with { Sha1 = new string('0', 40) }));

        DatVerificationResult result = XisoDat.Verify(dat, [isoA, isoB]);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Verified);
        Assert.Equal(0, result.Ambiguous);
        Assert.Equal(0, result.NotInDat);
        Assert.Equal(1, result.Missing);
        Assert.Equal("Never Supplied", result.MissingRoms[0].GameName);
    }

    [Fact]
    public void Verify_MismatchedImageIsNotInDat()
    {
        string work = CreateTempDir("xiso_dat");
        string iso = CreateIso(work, "alpha");
        XisoImageHashResult hash = XisoImageHash.Compute(iso);
        string dat = WriteDat(work, "games.dat",
            ("Alpha Game", Path.GetFileName(iso), hash with { Md5 = new string('0', 32) }));

        DatVerificationResult result = XisoDat.Verify(dat, [iso]);

        Assert.False(result.Passed);
        Assert.Equal(0, result.Verified);
        Assert.Equal(1, result.NotInDat);
        Assert.Empty(result.Images[0].Matches);
    }

    [Fact]
    public void Verify_DuplicateDatEntriesAreAmbiguous()
    {
        string work = CreateTempDir("xiso_dat");
        string iso = CreateIso(work, "alpha");
        XisoImageHashResult hash = XisoImageHash.Compute(iso);
        string dat = WriteDat(work, "games.dat",
            ("Alpha Game", Path.GetFileName(iso), hash),
            ("Alpha Game (duplicate)", Path.GetFileName(iso), hash));

        DatVerificationResult result = XisoDat.Verify(dat, [iso]);

        Assert.False(result.Passed);
        Assert.Equal(0, result.Verified);
        Assert.Equal(1, result.Ambiguous);
        Assert.Empty(result.MissingRoms);
    }

    [Fact]
    public void Parse_RejectsNonDatRoot()
    {
        string work = CreateTempDir("xiso_dat");
        string dat = Path.Combine(work, "bad.dat");
        File.WriteAllText(dat, "<not-a-dat />", Encoding.UTF8);

        Assert.Throws<InvalidDataException>(() => XisoDat.Parse(dat));
    }

    [Fact]
    public void Cli_VerifyDat_TextAndJson()
    {
        string work = CreateTempDir("xiso_dat");
        string iso = CreateIso(work, "alpha");
        XisoImageHashResult hash = XisoImageHash.Compute(iso);
        string dat = WriteDat(work, "games.dat",
            ("Alpha Game", Path.GetFileName(iso), hash));

        (int rc, string stdout, string error) = Run(["verify-dat", dat, iso]);
        Assert.Empty(error);
        Assert.Equal(0, rc);
        Assert.Contains("VERIFIED", stdout, StringComparison.Ordinal);
        Assert.Contains("Result: PASS", stdout, StringComparison.Ordinal);

        (int jsonRc, string jsonOut, string jsonError) = Run(["verify-dat", dat, iso, "--json"]);
        Assert.Empty(jsonError);
        Assert.Equal(0, jsonRc);
        Assert.Contains("\"verified\":1", jsonOut, StringComparison.Ordinal);
        Assert.Contains("\"passed\":true", jsonOut, StringComparison.Ordinal);
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        int rc = Program.Main(args);
        return (rc, _out.ToString(), _err.ToString());
    }

    private string CreateIso(string work, string name)
    {
        string source = CreateTempDir($"xiso_dat_src_{name}");
        File.WriteAllText(Path.Combine(source, "file.txt"), $"payload {name}");
        string iso = Path.Combine(work, name + ".iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        return iso;
    }

    private string WriteDat(string work, string name, params (string Game, string File, XisoImageHashResult Hash)[] entries)
    {
        StringBuilder xml = new();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<datafile>");
        xml.AppendLine("  <header><name>Synthetic DAT</name></header>");
        foreach ((string game, string file, XisoImageHashResult hash) in entries)
        {
            xml.AppendLine($"  <game name=\"{game}\">");
            xml.AppendLine(
                System.Globalization.CultureInfo.InvariantCulture,
                $"    <rom name=\"{file}\" size=\"{hash.TotalBytes}\" md5=\"{hash.Md5}\" " +
                $"sha1=\"{hash.Sha1}\" crc=\"{hash.Crc32}\" />");
            xml.AppendLine("  </game>");
        }

        xml.AppendLine("</datafile>");
        string path = Path.Combine(work, name);
        File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));
        return path;
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
