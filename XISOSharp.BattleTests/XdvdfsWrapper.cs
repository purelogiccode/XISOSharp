using System.Diagnostics;
using System.Text;

namespace XISOSharp.BattleTests;

/// <summary>
/// Thin wrapper around the reference <c>xdvdfs.exe</c> (xdvdfs-cli 0.8.3, MIT)
/// for extended battle comparisons of the xdvdfs-borrowed features
/// (checksum, unpack, pack, tree, copy-out, md5).
/// xdvdfs has no partition probing: it only handles plain XISO images, so the
/// battle runs it against game-partition splits and created ISOs, never raw Redump.
/// </summary>
internal sealed class XdvdfsWrapper : IDisposable
{
    private readonly string _exePath;

    /// <summary>Gets whether the oracle exe exists.</summary>
    public bool Available => File.Exists(_exePath);

    /// <summary>Initializes wrapper with path to xdvdfs.exe.</summary>
    public XdvdfsWrapper(string exePath)
    {
        _exePath = Path.GetFullPath(exePath);
    }

    /// <summary>Runs the exe with args, returns exit code and stdout/stderr.</summary>
    public (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
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
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start xdvdfs.exe");
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
            throw new TimeoutException($"xdvdfs.exe timed out after {timeoutMs} ms.");
        }

        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Deterministic content checksum (<c>checksum -s</c> prints <c>&lt;hex&gt;\t&lt;path&gt;</c>).</summary>
    public (int ExitCode, string StdOut, string StdErr) Checksum(string imagePath) => Run("checksum", "-s", imagePath);

    /// <summary>Unpacks an entire image to a directory.</summary>
    public (int ExitCode, string StdOut, string StdErr) Unpack(string imagePath, string outDir) => Run("unpack", imagePath, outDir);

    /// <summary>Packs an image from a directory (or source ISO).</summary>
    public (int ExitCode, string StdOut, string StdErr) Pack(string sourcePath, string imagePath) => Run("pack", sourcePath, imagePath);

    /// <summary>Copies a file or directory out of the image.</summary>
    public (int ExitCode, string StdOut, string StdErr) CopyOut(string imagePath, string srcPath, string destPath) => Run("copy-out", imagePath, srcPath, destPath);

    /// <summary>MD5 of a file (or every entry when <paramref name="innerPath"/> is null).</summary>
    public (int ExitCode, string StdOut, string StdErr) Md5(string imagePath, string? innerPath = null) => innerPath == null ? Run("md5", imagePath) : Run("md5", imagePath, innerPath);

    /// <summary>Recursive listing (<c>/path (N bytes)</c> lines + totals).</summary>
    public (int ExitCode, string StdOut, string StdErr) Tree(string imagePath) => Run("tree", imagePath);

    /// <summary>Version line for reports.</summary>
    public string GetVersion()
    {
        try
        {
            (int code, string so, string se) = Run("-V");
            string txt = string.IsNullOrWhiteSpace(so) ? se : so;
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
