using System.Diagnostics;
using System.Text;

namespace XISOSharp.BattleTests;

/// <summary>Thin wrapper around the native <c>extract-xiso.exe</c> (v2.7.1) for battle comparisons.</summary>
internal sealed class ExtractXisoWrapper : IDisposable
{
    private readonly string _exePath;

    /// <summary>Gets whether the native exe exists and is runnable.</summary>
    public bool Available => File.Exists(_exePath);

    /// <summary>Initializes wrapper with path to extract-xiso.exe.</summary>
    public ExtractXisoWrapper(string exePath)
    {
        _exePath = Path.GetFullPath(exePath);
    }

    /// <summary>Runs the exe with args, returns exit code and stdout/stderr.</summary>
    public (int ExitCode, string StdOut, string StdErr) Run(params string[] args) => RunCore(null, args);

    /// <summary>
    /// Runs the exe with a per-process working directory (BTL-011: no
    /// process-wide <c>Directory.SetCurrentDirectory</c> mutation).
    /// </summary>
    public (int ExitCode, string StdOut, string StdErr) RunInDirectory(string workingDirectory, params string[] args) => RunCore(workingDirectory, args);

    private (int ExitCode, string StdOut, string StdErr) RunCore(string? workingDirectory, string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start extract-xiso.exe");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        const int timeoutMs = 600_000;
        if (!proc.WaitForExit(timeoutMs))
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

            proc.WaitForExit(5000);
            throw new TimeoutException($"extract-xiso.exe timed out after {timeoutMs} ms.");
        }

        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Runs quiet (-Q) variant.</summary>
    public (int ExitCode, string StdOut, string StdErr) RunQuiet(params string[] args)
    {
        List<string> list = new(args) { "-Q" };
        return Run(list.ToArray());
    }

    /// <summary>Lists files via <c>-l</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) ListFiles(string isoPath) => Run("-l", isoPath);

    /// <summary>Extracts via <c>-x -d &lt;out&gt;</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) ExtractFiles(string isoPath, string outDir) => Run("-x", "-d", outDir, isoPath);

    /// <summary>Rewrites via <c>-r -d &lt;out&gt;</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) Rewrite(string isoPath, string outDir) => Run("-r", "-d", outDir, isoPath);

    /// <summary>Creates via <c>-c &lt;dir&gt; [name]</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) Create(string dir, string? outName = null)
    {
        if (outName != null) return Run("-c", dir, outName);
        return Run("-c", dir);
    }

    /// <summary>Gets version via <c>-v</c>.</summary>
    public string GetVersion()
    {
        (int code, string so, string se) = Run("-v");
        string txt = string.IsNullOrWhiteSpace(so) ? se : so;
        return txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? $"exit:{code}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
