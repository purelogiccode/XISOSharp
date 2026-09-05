using System.Diagnostics;
using System.Text;

namespace XISOSharp.BattleTests;

/// <summary>
/// Thin wrapper around the reference <c>xboxkit.exe</c> (XboxKit by Deterous, MIT)
/// for extended battle comparisons of the XboxKit-borrowed archival features
/// (video/xiso/filler/seed/update split, wipe/trim, petrify, ZAR, rebuild).
/// XboxKit exit codes are unreliable (0 even on [ERROR]), so battles must
/// compare output artifacts, never exit codes.
/// </summary>
internal sealed class XboxKitWrapper : IDisposable
{
    private readonly string _exePath;

    /// <summary>Gets whether the oracle exe exists.</summary>
    public bool Available => File.Exists(_exePath);

    /// <summary>Initializes wrapper with path to xboxkit.exe.</summary>
    public XboxKitWrapper(string exePath)
    {
        _exePath = Path.GetFullPath(exePath);
    }

    /// <summary>Runs the exe with args in <paramref name="workDir"/> (outputs land next to inputs).</summary>
    public (int ExitCode, string StdOut, string StdErr) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start xboxkit.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Full lossless split (<c>-a</c> = -rstuvwx): xiso + video + filler + seed/update.</summary>
    public (int ExitCode, string StdOut, string StdErr) SplitAll(string workDir, string isoPath)
    {
        return Run(workDir, "-y", "-q", "-a", isoPath);
    }

    /// <summary>Best-effort trim/wipe split (<c>-b</c> = -twx).</summary>
    public (int ExitCode, string StdOut, string StdErr) SplitBest(string workDir, string isoPath)
    {
        return Run(workDir, "-y", "-q", "-b", isoPath);
    }

    /// <summary>Rebuild mode: <c>xboxkit &lt;input.xiso&gt; [files...]</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) Rebuild(string workDir, params string[] parts)
    {
        var args = new List<string> { "-y", "-q" };
        args.AddRange(parts);
        return Run(workDir, args.ToArray());
    }

    /// <summary>Banner line for reports (xboxkit has no --version).</summary>
    public string GetVersion()
    {
        try
        {
            (var code, var so, var se) = Run(Path.GetTempPath(), "--help");
            var txt = string.IsNullOrWhiteSpace(so) ? se : so;
            return txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                ?? $"exit:{code}";
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.Message}";
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
