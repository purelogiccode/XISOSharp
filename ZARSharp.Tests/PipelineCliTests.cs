using ZARSharp.Pipeline;

namespace ZARSharp.Tests;

/// <summary>
/// Tests for <see cref="ZarchiveCli"/> (the callable
/// <c>zarchive.exe input [output]</c> contract) and <see cref="ProcessRunner"/>
/// (the <c>core.py::_run_cmd</c> port: <c>(\d+)%</c> parsing, exit mapping,
/// tool launching).
/// </summary>
public sealed class PipelineCliTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
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
                // ignored
            }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class LogSink
    {
        public readonly List<string> Lines = [];
        private readonly object _gate = new();

        public void Log(string line)
        {
            lock (_gate)
            {
                Lines.Add(line);
            }
        }
    }

    // ------------------------------------------------------------------
    // zarchive.exe contract
    // ------------------------------------------------------------------

    [Fact]
    public void Cli_NoArgs_PrintsUsageAndOk()
    {
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("zarchive.exe input_path", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_TooManyArgs_ReturnsBadUsage()
    {
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.BadUsage, ZarchiveCli.Run(["a", "b", "c"], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("Too many paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_PackDir_DefaultNameAndLog()
    {
        var root = NewTempDir("cli_pack");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src], log: sink.Log));

        Assert.True(File.Exists(Path.Combine(root, "game.zar")));
        Assert.Contains(sink.Lines, l => l.Contains("Outputting to:", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains("Adding a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_PackRefusesExisting_ReturnsRefused()
    {
        var root = NewTempDir("cli_refuse");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(root, "game.zar"), [9, 9, 9]);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Refused, ZarchiveCli.Run([src], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(root, "game.zar")));
    }

    [Fact]
    public void Cli_PackOutputIsDirectory_ReturnsNotFound()
    {
        var root = NewTempDir("cli_outdir");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var clash = Directory.CreateDirectory(Path.Combine(root, "clash")).FullName;

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.NotFound, ZarchiveCli.Run([src, clash], log: sink.Log));
    }

    [Fact]
    public void Cli_InvalidInput_ReturnsBadUsage()
    {
        var root = NewTempDir("cli_bad");
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.BadUsage,
            ZarchiveCli.Run([Path.Combine(root, "missing")], log: sink.Log));
    }

    [Fact]
    public void Cli_Extract_DefaultDirAndFileLog()
    {
        var root = NewTempDir("cli_ext");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "game.zar");
        ZarPipeline.Pack(src, zar);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zar], log: sink.Log));

        var outDir = Path.Combine(root, "game_extracted");
        Assert.Equal("hello", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Contains(sink.Lines, l => l.Contains("Extracting to:", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_ExtractOutputIsFile_ReturnsOutputNotDirectory()
    {
        var root = NewTempDir("cli_extbad");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "game.zar");
        ZarPipeline.Pack(src, zar);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "x");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.OutputNotDirectory, ZarchiveCli.Run([zar, blocker], log: sink.Log));
    }

    [Fact]
    public void Cli_RoundTrip_ExplicitPaths()
    {
        var root = NewTempDir("cli_rt");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllBytes(Path.Combine(src, "a.bin"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var zar = Path.Combine(root, "custom.zar");
        var dest = Path.Combine(root, "restored");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src, zar], log: sink.Log));
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zar, dest], log: sink.Log));
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "a.bin")), File.ReadAllBytes(Path.Combine(dest, "a.bin")));
    }

    [Fact]
    public void Cli_PackFailure_DeletesIncompleteOutput()
    {
        var root = NewTempDir("cli_incomplete");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "out.zar");

        var sink = new LogSink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // A cancelled pack surfaces the cancellation (not a masked exit code).
        Assert.Throws<OperationCanceledException>(() => ZarchiveCli.Run([src, zar], log: sink.Log, cancellationToken: cts.Token));
        Assert.False(File.Exists(zar));
    }

    // ------------------------------------------------------------------
    // ProcessRunner
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("42%", 0.42)]
    [InlineData("7z 100%", 1.0)]
    [InlineData("  7% done", 0.07)]
    [InlineData("1%2%", 0.01)]
    [InlineData("150%", 1.0)]
    public void Runner_ParseProgress_FirstMatchWins(string line, double expected)
    {
        var actual = ProcessRunner.TryParseProgressLine(line);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 9);
    }

    [Theory]
    [InlineData("Adding foo/bar.txt")]
    [InlineData("")]
    [InlineData("no digits here")]
    [InlineData("% dangling")]
    public void Runner_ParseProgress_NoMatch_ReturnsNull(string line)
    {
        Assert.Null(ProcessRunner.TryParseProgressLine(line));
    }

    [Fact]
    public void Runner_ExitMapping_AcceptsZeroAndOne()
    {
        ProcessRunner.ThrowIfFailed(0, null, "tool");
        ProcessRunner.ThrowIfFailed(1, "warning", "tool");
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessRunner.ThrowIfFailed(2, "boom", "tool"));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_MissingBinary_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(
            () => ProcessRunner.Run("definitely-not-a-tool-xyz-123"));
    }

    [Fact]
    public void Runner_PreCancelled_DoesNotRun()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(
                () => ProcessRunner.Run("dotnet", "--version", cancellationToken: cts.Token));
        }
        catch (FileNotFoundException)
        {
            // dotnet itself missing: environment cannot run tools; nothing to assert.
        }
    }

    [Fact]
    public void Runner_Smoke_DotnetVersion_ExitZero()
    {
        double? last = null;
        var progress = new SmokeProgress(v => last = v);
        ProcessRunner.ProcessResult result;
        try
        {
            result = ProcessRunner.Run("dotnet", "--version", progress: progress);
        }
        catch (FileNotFoundException)
        {
            // dotnet itself missing: environment cannot run tools; nothing to assert.
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.LastLine));
        Assert.Null(last);
    }

    private sealed class SmokeProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
