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

    public ToolProcess(string exePath) => ExePath = Path.GetFullPath(exePath);

    /// <summary>Runs the exe with args; returns exit code and captured output.</summary>
    public (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
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

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {ExePath}");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutMs))
        {
            TryKill(proc);
            proc.WaitForExit(5000);
            throw new TimeoutException($"{Path.GetFileName(ExePath)} timed out after {TimeoutMs} ms: {string.Join(' ', args)}");
        }

        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>Probes the tool banner via -v (first non-empty line).</summary>
    public string GetVersion()
    {
        try
        {
            (int code, string so, string se) = Run("-v");
            string txt = string.IsNullOrWhiteSpace(so) ? se : so;
            return txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? $"exit:{code}";
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
