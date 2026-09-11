using System.Diagnostics;
using System.Text;

namespace XISOSharp.BattleTests;

/// <summary>
/// Generic CLI runner: starts an exe, captures stdout/stderr, enforces a timeout
/// with full-tree kill. Used for both XISOSharp.Cli.exe and extract-xiso.exe.
/// </summary>
internal sealed class ToolProcess
{
    /// <summary>Gets the per-run timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 3_600_000;

    /// <summary>Gets whether the exe exists on disk.</summary>
    public bool Available => File.Exists(ExePath);

    /// <summary>Gets the resolved exe path.</summary>
    public string ExePath { get; }

    public ToolProcess(string exePath)
    {
        ExePath = Path.GetFullPath(exePath);
    }

    /// <summary>Runs the exe with args; returns exit code, captured output, and the
    /// exe's wall-clock seconds (start → exit, excludes harness overhead).</summary>
    public (int ExitCode, string StdOut, string StdErr, double Seconds) Run(params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Stopwatch sw = Stopwatch.StartNew();
        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {ExePath}");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutMs))
        {
            TryKill(proc);
            proc.WaitForExit(5000);
            throw new TimeoutException(
                $"{Path.GetFileName(ExePath)} timed out after {TimeoutMs} ms: {string.Join(' ', args)}");
        }

        sw.Stop();
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult(),
            sw.Elapsed.TotalSeconds);
    }

    /// <summary>Probes the tool banner: -v first, then --version, then --help
    /// (first non-empty line). Tolerates CLIs with different version flags.</summary>
    public string GetVersion()
    {
        try
        {
            foreach (string flag in new[] { "-v", "--version", "--help" })
            {
                (int code, string so, string se, _) = Run(flag);
                string txt = string.IsNullOrWhiteSpace(so) ? se : so;
                string first = txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ??
                               string.Empty;
                if (code == 0 && !string.IsNullOrWhiteSpace(first) &&
                    !first.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                {
                    return first;
                }
            }

            return "version probe failed";
        }
        catch (Exception ex)
        {
            return $"version probe failed: {ex.GetType().Name}";
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between WaitForExit and Kill.
        }
        catch (NotSupportedException)
        {
            try
            {
                proc.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
