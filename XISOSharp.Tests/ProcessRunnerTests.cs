using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="ProcessRunner"/> and <see cref="ProcessRunResult"/>:
/// start failures, output capture, line callbacks, exit codes, cancellation, and timeouts.
/// </summary>
public class ProcessRunnerTests
{
    private static (string FileName, string[] Args) ShellCommand(string command) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", command])
            : ("sh", ["-c", command]);

    private static (string FileName, string[] Args) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", "ping -n 30 127.0.0.1"])
            : ("sh", ["-c", "sleep 30"]);

    /// <summary>Verifies the default values of a fresh <see cref="ProcessRunResult"/>.</summary>
    [Fact]
    public void ProcessRunResult_Defaults()
    {
        ProcessRunResult result = new();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    /// <summary>Verifies a real process run captures stdout and reports exit code 0.</summary>
    [Fact]
    public async Task RunAsync_DotnetVersion_ReturnsZeroWithStdout()
    {
        ProcessRunResult result = await ProcessRunner.RunAsync("dotnet", ["--version"]);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    /// <summary>Verifies stdout and stderr are captured and both reach the line callback.</summary>
    [Fact]
    public async Task RunAsync_CapturesStdoutStderrAndLines()
    {
        (string file, string[] args) = ShellCommand("echo hello & echo err 1>&2");
        List<string> lines = [];
        ProcessRunResult result = await ProcessRunner.RunAsync(file, args, onLine: lines.Add);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
        Assert.Contains("err", result.StandardError);
        Assert.Contains(lines, l => l.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("err", StringComparison.Ordinal));
    }

    /// <summary>Verifies a non-zero exit code is propagated unchanged.</summary>
    [Fact]
    public async Task RunAsync_NonZeroExit_IsReported()
    {
        (string file, string[] args) = ShellCommand("exit 7");
        ProcessRunResult result = await ProcessRunner.RunAsync(file, args);
        Assert.Equal(7, result.ExitCode);
    }

    /// <summary>Verifies a missing executable yields exit code -1 with the error message.</summary>
    [Fact]
    public async Task RunAsync_MissingExecutable_ReturnsMinusOne()
    {
        ProcessRunResult result = await ProcessRunner.RunAsync("xisosharp-no-such-executable-xyz", []);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
    }

    /// <summary>Verifies a null file name is rejected.</summary>
    [Fact]
    public async Task RunAsync_NullFileName_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ProcessRunner.RunAsync(null!, []));
    }

    /// <summary>Verifies an empty file name is rejected.</summary>
    [Fact]
    public async Task RunAsync_EmptyFileName_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ProcessRunner.RunAsync(string.Empty, []));
    }

    /// <summary>Verifies a null argument list is rejected.</summary>
    [Fact]
    public async Task RunAsync_NullArgs_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ProcessRunner.RunAsync("dotnet", null!));
    }

    /// <summary>Verifies a pre-canceled token aborts the run with an operation-canceled error.</summary>
    [Fact]
    public async Task RunAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        (string file, string[] args) = LongRunningCommand();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(file, args, cancellationToken: cts.Token));
    }

    /// <summary>Verifies an expired timeout kills the process and throws <see cref="TimeoutException"/>.</summary>
    [Fact]
    public async Task RunAsync_Timeout_ThrowsTimeoutException()
    {
        (string file, string[] args) = LongRunningCommand();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync(file, args, timeout: TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Verifies <see cref="Timeout.InfiniteTimeSpan"/> disables the timeout path.</summary>
    [Fact]
    public async Task RunAsync_InfiniteTimeout_WaitsForCompletion()
    {
        (string file, string[] args) = ShellCommand("exit 0");
        ProcessRunResult result = await ProcessRunner.RunAsync(file, args, timeout: Timeout.InfiniteTimeSpan);
        Assert.Equal(0, result.ExitCode);
    }
}
