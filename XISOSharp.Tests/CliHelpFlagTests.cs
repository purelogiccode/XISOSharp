using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <c>--help</c> flag (bug-report ID 66717): <c>--help</c> was
/// previously missing from the main parser switch (only <c>-h</c> was listed),
/// so <c>XISOSharp --help</c> fell through to the positional path and was
/// probed as an image (<c>open error: --help: No such file or directory</c>).
/// Also covers the misplaced-flag refusal for a <c>--help</c> trailing a
/// filename, mirroring the -d/-y handling (upstream #61). CLI runs go through
/// <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class CliHelpFlagTests : IDisposable
{
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly TextWriter _savedLoggerOut;
    private readonly TextWriter _savedLoggerError;

    public CliHelpFlagTests()
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
        _outCapture.Dispose();
        _errCapture.Dispose();
    }

    [Fact]
    public void Cli_Version_ShortFlag_PrintsXisoSharpBanner()
    {
        int rc = Program.Main(["-v"]);

        Assert.Equal(0, rc);
        string outText = _outCapture.ToString();
        Assert.StartsWith("XISOSharp v", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("extract-xiso", outText, StringComparison.Ordinal);
        Assert.Matches(@"^XISOSharp v\S+ for (win|linux|macos|cross-platform)", outText.TrimEnd());
    }

    [Fact]
    public void Cli_Help_LongFlag_PrintsUsageAndExitsZero()
    {
        int rc = Program.Main(["--help"]);

        Assert.Equal(0, rc);
        Assert.Contains("Usage:", _errCapture.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("open error", _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Help_LongFlagBeforeFilenames_PrintsUsageAndExitsZero()
    {
        // --help wins regardless of what follows: it is a flag, not a file.
        int rc = Program.Main(["--help", "game.iso"]);

        Assert.Equal(0, rc);
        Assert.Contains("Usage:", _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Help_ShortFlag_StillPrintsUsageAndExitsZero()
    {
        int rc = Program.Main(["-h"]);

        Assert.Equal(0, rc);
        Assert.Contains("Usage:", _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Help_LongFlagAfterIso_ReportsMisplacedFlag()
    {
        // The misplaced-flag guard runs before any file open, so no fixture is
        // needed: the token is refused as a flag, not probed as an image.
        int rc = Program.Main(["game.iso", "--help"]);

        Assert.Equal(1, rc);
        string err = _errCapture.ToString();
        Assert.Contains("--help", err, StringComparison.Ordinal);
        Assert.Contains("must come before", err, StringComparison.Ordinal);
        Assert.DoesNotContain("open error", err, StringComparison.Ordinal);
    }
}
