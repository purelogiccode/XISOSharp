using System.Security.Cryptography;
using System.Text.Json;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Validation with ZArchive (<c>.zar</c>) inputs (PlanToEnhance Phase 3):
/// <see cref="XisoValidator.ValidateConversion"/> compares a ZAR name tree with
/// a sector image, and the CLI <c>validate</c> verb accepts <c>.zar</c> on
/// either side.
/// </summary>
[Collection("Sequential")]
public sealed class ZarValidateTests : IDisposable
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
    public void ValidateConversion_ZarToIso_Passes()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        ValidationResult result = XisoValidator.ValidateConversion(zar, iso);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
        Assert.Equal(result.SourceFileCount, result.OutputFileCount);
        Assert.Equal(result.SourceTotalBytes, result.OutputTotalBytes);
        Assert.True(result.SourceFileCount > 0);
    }

    [Fact]
    public void ValidateConversion_IsoToZar_Passes()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        ValidationResult result = XisoValidator.ValidateConversion(iso, zar, verifyChecksums: true);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateConversion_ZarToIso_WithChecksums_Passes()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        ValidationResult result = XisoValidator.ValidateConversion(zar, iso, verifyChecksums: true);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateConversion_ZarToIso_DetectsChecksumMismatch()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        // Same tree, same sizes, one changed byte: only a checksum compare can
        // tell the images apart.
        string modifiedSrc = Path.Combine(work, "src2");
        CopyDirectory(Path.Combine(work, "src"), modifiedSrc);
        byte[] bytes = File.ReadAllBytes(Path.Combine(modifiedSrc, "a.txt"));
        bytes[0] = bytes[0] == (byte)'X' ? (byte)'Y' : (byte)'X';
        File.WriteAllBytes(Path.Combine(modifiedSrc, "a.txt"), bytes);
        string modifiedIso = CreateIso(work, "src2", "modified");

        ValidationResult result = XisoValidator.ValidateConversion(zar, modifiedIso, verifyChecksums: true);

        Assert.False(result.Passed);
        ValidationIssue issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationIssueType.ChecksumMismatch, issue.Type);
        Assert.Equal("/a.txt", issue.Path);
        Assert.NotNull(issue.SourceHash);
        Assert.NotNull(issue.OutputHash);
    }

    [Fact]
    public void ValidateConversion_ZarToIso_DetectsMissingFile()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string fullIso = CreateIso(work, "src");
        string fullZar = CreateZar(fullIso, Path.Combine(work, "full.zar"));

        string reducedSrc = Path.Combine(work, "reduced");
        CopyDirectory(Path.Combine(work, "src"), reducedSrc);
        File.Delete(Path.Combine(reducedSrc, "a.txt"));
        string reducedIso = CreateIso(work, "reduced", "reduced");

        ValidationResult result = XisoValidator.ValidateConversion(fullZar, reducedIso);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, static i =>
            i.Type == ValidationIssueType.MissingInOutput && string.Equals(i.Path, "/a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeFileHash_Zar_MatchesIsoHash()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        foreach (HashAlgorithmName algorithm in new[] { HashAlgorithmName.SHA256, HashAlgorithmName.MD5 })
        {
            byte[]? isoHash = XisoReader.ComputeFileHash(iso, "/a.txt", algorithm);
            byte[]? zarHash = XisoZarchive.ComputeFileHash(zar, "/a.txt", algorithm);
            Assert.NotNull(isoHash);
            Assert.NotNull(zarHash);
            Assert.Equal(isoHash, zarHash);

            // Paths are case-insensitive, like the XDVDFS tree.
            Assert.Equal(zarHash, XisoZarchive.ComputeFileHash(zar, "\\A.TXT", algorithm));
        }
    }

    [Fact]
    public void ComputeFileHash_Zar_MissingOrDirectory()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        Assert.Null(XisoZarchive.ComputeFileHash(zar, "/nope.txt", HashAlgorithmName.SHA256));
        Assert.Null(XisoZarchive.ComputeFileHash(zar, "/a.txt/child", HashAlgorithmName.SHA256));
        Assert.Throws<InvalidDataException>(() =>
            XisoZarchive.ComputeFileHash(zar, "/sub", HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Cli_Validate_ZarVsIso_Passes()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        (int rc, string stdout, string stderr) = Run("validate", zar, iso);

        Assert.Equal(0, rc);
        Assert.Contains("RESULT: PASS", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Validate_ZarVsIso_JsonAndReport()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string report = Path.Combine(work, "report.json");

        (int rc, string stdout, _) = Run("validate", "--json", "--validate-report", report, zar, iso);

        Assert.Equal(0, rc);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.Equal("validate", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal(zar, root.GetProperty("source").GetString());
        Assert.Equal(iso, root.GetProperty("output").GetString());
        Assert.Equal(0, root.GetProperty("issues").GetArrayLength());

        Assert.True(File.Exists(report));
        using JsonDocument reportDoc = JsonDocument.Parse(File.ReadAllText(report));
        Assert.True(reportDoc.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Cli_SectorOnlyVerb_OnZar_FailsCleanly()
    {
        string work = CreateTempDir("xiso_zar_validate");
        string iso = CreateIso(work, "src");
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        (int rc, _, string stderr) = Run("--sector-layout", zar);

        Assert.Equal(1, rc);
        Assert.Contains("not sector images", stderr, StringComparison.Ordinal);
        // A .zar handed to a sector verb is a user error, not a crash: no
        // "Fatal error"/"Unhandled CLI exception" report and no stack trace.
        Assert.DoesNotContain("Fatal error", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled CLI exception", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at XISOSharp.", stderr, StringComparison.Ordinal);
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

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateIso(string work, string sourceName, string isoName = "game")
    {
        string source = Path.Combine(work, sourceName);
        if (!Directory.Exists(source)) CreateSourceTree(source);
        string isoDir = Path.Combine(work, "iso_" + isoName);
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(source, isoDir, null, null,
            out string? isoPath, isoName + ".iso", null, fileTime: 0));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static void CreateSourceTree(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(source, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "c.txt"), "nested");
    }

    private static string CreateZar(string iso, string zar)
    {
        Assert.True(XisoZarchive.CreateZar(iso, zar, quiet: true));
        return zar;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
