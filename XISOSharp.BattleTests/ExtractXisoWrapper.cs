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
    public (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start extract-xiso.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Runs quiet (-Q) variant.</summary>
    public (int ExitCode, string StdOut, string StdErr) RunQuiet(params string[] args)
    {
        var list = new List<string>(args) { "-Q" };
        return Run(list.ToArray());
    }

    /// <summary>Lists files via <c>-l</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) ListFiles(string isoPath)
    {
        return Run("-l", isoPath);
    }

    /// <summary>Extracts via <c>-x -d &lt;out&gt;</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) ExtractFiles(string isoPath, string outDir)
    {
        return Run("-x", "-d", outDir, isoPath);
    }

    /// <summary>Rewrites via <c>-r -d &lt;out&gt;</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) Rewrite(string isoPath, string outDir)
    {
        return Run("-r", "-d", outDir, isoPath);
    }

    /// <summary>Creates via <c>-c &lt;dir&gt; [name]</c>.</summary>
    public (int ExitCode, string StdOut, string StdErr) Create(string dir, string? outName = null)
    {
        if (outName != null) return Run("-c", dir, outName);
        return Run("-c", dir);
    }

    /// <summary>Gets version via <c>-v</c>.</summary>
    public string GetVersion()
    {
        (var code, var so, var se) = Run("-v");
        var txt = string.IsNullOrWhiteSpace(so) ? se : so;
        return txt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? $"exit:{code}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}