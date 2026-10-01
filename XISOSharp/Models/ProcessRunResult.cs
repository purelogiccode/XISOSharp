namespace XISOSharp;

/// <summary>
/// Result of a <see cref="XISOSharp.ProcessRunner"/> execution: exit code plus captured output.
/// </summary>
public sealed class ProcessRunResult
{
    /// <summary>Gets the process exit code, or -1 when it could not start or already exited unknown.</summary>
    public int ExitCode { get; init; }

    /// <summary>Gets the captured standard-output text (lines joined with newlines).</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Gets the captured standard-error text.</summary>
    public string StandardError { get; init; } = string.Empty;
}
