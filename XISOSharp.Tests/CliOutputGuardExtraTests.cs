using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Additional unit tests for <see cref="CliOutputGuard"/>: misplaced-flag detection
/// and the input==output refusal rules, complementing <c>CliOutputGuardTests</c>.
/// </summary>
public class CliOutputGuardExtraTests
{
    /// <summary>Verifies null, empty, and unknown tokens are not flagged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("game.iso")]
    [InlineData("-z")]
    public void CheckMisplacedFlag_NonFlags_ReturnsNull(string? token)
    {
        Assert.Null(CliOutputGuard.CheckMisplacedFlag(token));
    }

    /// <summary>Verifies verb-only spellings are deliberately not flagged.</summary>
    [Theory]
    [InlineData("-O")]
    [InlineData("--output")]
    public void CheckMisplacedFlag_VerbOnlySpellings_ReturnNull(string token)
    {
        Assert.Null(CliOutputGuard.CheckMisplacedFlag(token));
    }

    /// <summary>Verifies known main-parser flags in a positional slot are flagged.</summary>
    [Theory]
    [InlineData("-d")]
    [InlineData("--batch")]
    [InlineData("--repair")]
    [InlineData("validate")]
    public void CheckMisplacedFlag_KnownFlags_ReturnError(string token)
    {
        string? error = CliOutputGuard.CheckMisplacedFlag(token);
        Assert.NotNull(error);
        Assert.Contains(token, error, StringComparison.Ordinal);
        Assert.Contains("must come before ISO filenames", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies a blank rewrite output is always safe.</summary>
    [Fact]
    public void CheckRewriteOutput_BlankOutput_ReturnsNull()
    {
        Assert.Null(CliOutputGuard.CheckRewriteOutput("game.iso", null));
        Assert.Null(CliOutputGuard.CheckRewriteOutput("game.iso", "   "));
    }

    /// <summary>Verifies the rewrite output cannot be the input itself.</summary>
    [Fact]
    public void CheckRewriteOutput_SameFile_ReturnsError()
    {
        string? error = CliOutputGuard.CheckRewriteOutput("game.iso", "./game.iso");
        Assert.NotNull(error);
        Assert.Contains("same file as the input", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies the rewrite output cannot be the <c>.old</c> backup.</summary>
    [Fact]
    public void CheckRewriteOutput_BackupName_ReturnsError()
    {
        string? error = CliOutputGuard.CheckRewriteOutput("game.iso", "game.iso.old");
        Assert.NotNull(error);
        Assert.Contains("backup", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies a distinct rewrite output is accepted.</summary>
    [Fact]
    public void CheckRewriteOutput_DistinctOutput_ReturnsNull()
    {
        Assert.Null(CliOutputGuard.CheckRewriteOutput("game.iso", "other.iso"));
    }

    /// <summary>Verifies single-input outputs cannot equal the input.</summary>
    [Fact]
    public void CheckSingleInputOutput_SameFile_ReturnsError()
    {
        string? error = CliOutputGuard.CheckSingleInputOutput("game.iso", "./game.iso");
        Assert.NotNull(error);
        Assert.Contains("same file as the input", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies blank or distinct single-input outputs are accepted.</summary>
    [Fact]
    public void CheckSingleInputOutput_SafeOutputs_ReturnNull()
    {
        Assert.Null(CliOutputGuard.CheckSingleInputOutput("game.iso", null));
        Assert.Null(CliOutputGuard.CheckSingleInputOutput("game.iso", "out.iso"));
    }

    /// <summary>Verifies the rebuild output cannot clobber any component input.</summary>
    [Fact]
    public void CheckRebuildOutput_ComponentCollision_ReturnsError()
    {
        string? error = CliOutputGuard.CheckRebuildOutput("out.iso", null, "x.iso", "video.bin", "./out.iso");
        Assert.NotNull(error);
        Assert.Contains("same file as input", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies the rebuild output cannot be the security-sectors file.</summary>
    [Fact]
    public void CheckRebuildOutput_SectorsCollision_ReturnsError()
    {
        string? error = CliOutputGuard.CheckRebuildOutput("out.iso", "out.iso", "x.iso");
        Assert.NotNull(error);
        Assert.Contains("sectors file", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies a distinct rebuild output is accepted.</summary>
    [Fact]
    public void CheckRebuildOutput_DistinctOutput_ReturnsNull()
    {
        Assert.Null(CliOutputGuard.CheckRebuildOutput("out.iso", "sectors.bin", "x.iso", "video.bin"));
    }

    /// <summary>Verifies image outputs cannot equal the source.</summary>
    [Fact]
    public void CheckImageOutput_SameFile_ReturnsError()
    {
        string? error = CliOutputGuard.CheckImageOutput("game.iso", "./game.iso");
        Assert.NotNull(error);
        Assert.Contains("same file as the input", error, StringComparison.Ordinal);
    }

    /// <summary>Verifies a distinct image output is accepted.</summary>
    [Fact]
    public void CheckImageOutput_DistinctOutput_ReturnsNull()
    {
        Assert.Null(CliOutputGuard.CheckImageOutput("game.iso", "game.cso"));
    }

    /// <summary>Verifies path comparison follows the OS case convention.</summary>
    [Fact]
    public void CheckImageOutput_CaseSensitivityFollowsOs()
    {
        string? error = CliOutputGuard.CheckImageOutput("Game.iso", "game.iso");
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(error);
        }
        else
        {
            Assert.Null(error);
        }
    }
}
