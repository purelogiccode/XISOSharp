using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// CLI contract regressions for flag placement/order (Todo #32/#33/#34/#36/#37):
/// mode-specific flags must be order-independent where documented and rejected
/// loudly where they cannot take effect. CLI runs go through
/// <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class CliFlagContractExtraTests : IDisposable
{
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    /// <summary>Redirects console and logger output for one test.</summary>
    public CliFlagContractExtraTests()
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

    /// <summary>Restores the redirected writers.</summary>
    public void Dispose()
    {
        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedLoggerOut;
        Logger.Error = _savedLoggerError;
        _outCapture.Dispose();
        _errCapture.Dispose();
    }

    private string AllError() => _errCapture + _outCapture.ToString();

    [Fact]
    public void Silent_BeforeChecksum_IsAccepted()
    {
        // Item #32: `--silent --checksum` used to fail while the reverse order
        // worked. The missing file still fails the run, but not on the flag.
        int rc = Program.Main(["--silent", "--checksum", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.DoesNotContain("--silent requires --checksum", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Silent_AfterChecksum_IsAccepted()
    {
        int rc = Program.Main(["--checksum", "--silent", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.DoesNotContain("--silent requires --checksum", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Silent_WithoutChecksum_IsRejected()
    {
        int rc = Program.Main(["--silent", "-l", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--silent requires --checksum", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Silent_BeforeVersion_IsRejected()
    {
        // -v/-h return before the post-parse validation, so they must run the
        // same check themselves.
        int rc = Program.Main(["--silent", "-v"]);

        Assert.Equal(1, rc);
        Assert.Contains("--silent requires --checksum", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Silent_BeforeHelp_IsRejected()
    {
        int rc = Program.Main(["--silent", "--help"]);

        Assert.Equal(1, rc);
        Assert.Contains("--silent requires --checksum", AllError(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--silent", "-v", "--checksum")]
    [InlineData("--silent", "--checksum", "-v")]
    [InlineData("--checksum", "-v", "--silent")]
    [InlineData("--silent", "-h", "")]
    [InlineData("-h", "--silent", "")]
    public void Silent_WithChecksum_BeforeVersionOrHelp_IsOrderIndependent(string first, string second, string third)
    {
        List<string> args = [first, second];
        if (third.Length > 0)
        {
            args.Add(third);
        }

        int rc = Program.Main([.. args]);
        bool withChecksum = args.Contains("--checksum");
        if (withChecksum)
        {
            // A valid --silent --checksum combination must reach the version/
            // help handling instead of failing on whichever flag was seen first.
            Assert.DoesNotContain("--silent requires --checksum", AllError(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(1, rc);
            Assert.Contains("--silent requires --checksum", AllError(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Jobs_WithCompressAlias_IsRejected()
    {
        // Item 37 follow-up: --compress expands to several modes, so --jobs can
        // never take effect; it used to be accepted and silently ignored.
        int rc = Program.Main(["--compress", "--jobs", "4", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs is only used with a lone --zar", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_WithMultiModeZar_IsRejected()
    {
        int rc = Program.Main(["--zar", "--petrify", "--jobs", "2", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs is only used with a lone --zar", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void FileTime_OutsideCreate_IsRejected()
    {
        int rc = Program.Main(["--file-time", "0", "-l", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--file-time is only used with -c", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void FileTime_WithPackIso_IsRejected()
    {
        // --pack <iso> translates to rewrite mode, which never consumes a file
        // time; the message names the create-only combinations.
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_cli_flag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string iso = Path.Combine(dir, "game.iso");
            File.WriteAllBytes(iso, new byte[64]);

            int rc = Program.Main(["--pack", iso, "--file-time", "0"]);

            Assert.Equal(1, rc);
            Assert.Contains("--file-time is only used with", AllError(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void PreserveAttrs_WithoutRewrite_IsRejected()
    {
        int rc = Program.Main(["--preserve-attrs", "-x", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--preserve-attrs is only used with -r", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_WithoutZar_IsRejected()
    {
        int rc = Program.Main(["--jobs", "4", "-l", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs/--policy are only used with --zar", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_WithoutZar_IsRejected()
    {
        int rc = Program.Main(["--policy", "skip", "-x", "no-such-flag-test.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs/--policy are only used with --zar", AllError(), StringComparison.Ordinal);
    }

    [Fact]
    public void Compress_SourceEqualsFirstSplitPart_IsRejected()
    {
        // Item #36: the base-name guard cannot see the .1.cso part, so the
        // library backstop used to be the only protection.
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_cli_flag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "game.1.cso");
            File.WriteAllBytes(source, [1, 2, 3]);

            int rc = Program.Main([
                "compress", "--split", "1000000", "-o",
                Path.Combine(dir, "game.cso"), source
            ]);

            Assert.Equal(1, rc);
            Assert.Contains("same file as the input", AllError(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
