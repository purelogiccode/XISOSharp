using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <c>-V</c> audit mode: it is an integrity audit, so a raw
/// (non-optimized) image passes with <c>Optimized: no</c> instead of failing on
/// the missing tag, while structural corruption still fails. CLI runs go
/// through <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class CliAuditTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    public CliAuditTests()
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

    private string CreateIso()
    {
        string src = CreateTempDir("xiso_cli_audit_src");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "audit me");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "nested.txt"), "nested");
        string outDir = CreateTempDir("xiso_cli_audit_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static void RemoveOptimizedTag(string isoPath)
    {
        using FileStream fs = new(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
        fs.Write(new byte[Constants.OptimizedTagLength]);
    }

    [Fact]
    public void Cli_Audit_OptimizedImage_ReportsYes()
    {
        string iso = CreateIso();

        Assert.Equal(0, Program.Main(["-V", iso]));

        string output = _outCapture.ToString();
        Assert.Contains("Optimized:      yes", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Audit_NonOptimizedImage_Passes()
    {
        string iso = CreateIso();
        RemoveOptimizedTag(iso);

        Assert.Equal(0, Program.Main(["-V", iso]));

        string output = _outCapture.ToString();
        Assert.Contains("Optimized:      no", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Optimized tag not found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Audit_CorruptTree_Fails()
    {
        string iso = CreateIso();
        RemoveOptimizedTag(iso);
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Seek(Constants.HeaderOffset + 20, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(0xFFFFFFF0u));
        }

        Assert.Equal(1, Program.Main(["-V", iso]));

        string output = _outCapture.ToString() + _errCapture;
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.Contains("exceeds file length", output, StringComparison.Ordinal);
    }
}
