using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the CLI input==output refusal rules (TODO #15, xdvdfs #36).
/// Pure path logic — no fixtures, no CLI run.
/// </summary>
public sealed class CliOutputGuardTests
{
    [Fact]
    public void CheckRewriteOutput_NullOutput_Allows()
    {
        Assert.Null(CliOutputGuard.CheckRewriteOutput("game.iso", null));
    }

    [Fact]
    public void CheckRewriteOutput_DistinctOutput_Allows()
    {
        Assert.Null(CliOutputGuard.CheckRewriteOutput("game.iso", "rewritten.iso"));
    }

    [Fact]
    public void CheckRewriteOutput_SameAsInput_Refuses()
    {
        var refusal = CliOutputGuard.CheckRewriteOutput("game.iso", "./game.iso");
        Assert.NotNull(refusal);
        Assert.Contains("omit -o", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRewriteOutput_SameAsBackup_Refuses()
    {
        var refusal = CliOutputGuard.CheckRewriteOutput("game.iso", "game.iso.old");
        Assert.NotNull(refusal);
        Assert.Contains(".old", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSingleInputOutput_Same_Refuses()
    {
        var refusal = CliOutputGuard.CheckSingleInputOutput("game.iso", "game.iso");
        Assert.NotNull(refusal);
        Assert.Contains("-o", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSingleInputOutput_Null_Allows()
    {
        Assert.Null(CliOutputGuard.CheckSingleInputOutput("game.iso", null));
    }

    [Fact]
    public void CheckRebuildOutput_OutputEqualsPart_RefusesAndNamesIt()
    {
        var refusal = CliOutputGuard.CheckRebuildOutput("game.xiso", null,
            "game.xiso", "game.video.iso", null, null);
        Assert.NotNull(refusal);
        Assert.Contains("game.xiso", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRebuildOutput_OutputEqualsSectorsFile_Refuses()
    {
        var refusal = CliOutputGuard.CheckRebuildOutput("sectors.txt", "sectors.txt",
            "game.xiso", "game.video.iso", null, null);
        Assert.NotNull(refusal);
        Assert.Contains("sectors", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRebuildOutput_Distinct_Allows()
    {
        Assert.Null(CliOutputGuard.CheckRebuildOutput("redump.iso", "sectors.txt",
            "game.xiso", "game.video.iso", null, null));
    }

    [Fact]
    public void CheckImageOutput_Same_Refuses()
    {
        var refusal = CliOutputGuard.CheckImageOutput("game.iso", "game.iso");
        Assert.NotNull(refusal);
        Assert.Contains("same file", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckImageOutput_Distinct_Allows()
    {
        Assert.Null(CliOutputGuard.CheckImageOutput("game.iso", "game.cso"));
    }

    [Theory]
    [InlineData("-d")]
    [InlineData("-o")]
    [InlineData("-x")]
    [InlineData("--skip-existing")]
    [InlineData("--batch")]
    public void CheckMisplacedFlag_KnownFlag_ReportsMustComeFirst(string token)
    {
        var refusal = CliOutputGuard.CheckMisplacedFlag(token);
        Assert.NotNull(refusal);
        Assert.Contains(token, refusal, StringComparison.Ordinal);
        Assert.Contains("must come before", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("./new/")]
    [InlineData("-Z")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData(null)]
    public void CheckMisplacedFlag_NotAFlag_Allows(string? token)
    {
        Assert.Null(CliOutputGuard.CheckMisplacedFlag(token));
    }
}
