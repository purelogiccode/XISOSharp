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

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start xdvdfs.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Deterministic content checksum (<c>checksum -s</c> prints <c>&lt;hex&gt;\t&lt;path&gt;</c>).</summary>
    public (int ExitCode, string StdOut, string StdErr) Checksum(string imagePath)
    {
        return Run("checksum", "-s", imagePath);
    }

    /// <summary>Unpacks an entire image to a directory.</summary>
    public (int ExitCode, string StdOut, string StdErr) Unpack(string imagePath, string outDir)
    {
        return Run("unpack", imagePath, outDir);
    }

    /// <summary>Packs an image from a directory (or source ISO).</summary>
    public (int ExitCode, string StdOut, string StdErr) Pack(string sourcePath, string imagePath)
    {
        return Run("pack", sourcePath, imagePath);
    }

    /// <summary>Copies a file or directory out of the image.</summary>
    public (int ExitCode, string StdOut, string StdErr) CopyOut(string imagePath, string srcPath, string destPath)
    {
        return Run("copy-out", imagePath, srcPath, destPath);
    }

    /// <summary>MD5 of a file (or every entry when <paramref name="innerPath"/> is null).</summary>
    public (int ExitCode, string StdOut, string StdErr) Md5(string imagePath, string? innerPath = null)
    {
        return innerPath == null ? Run("md5", imagePath) : Run("md5", imagePath, innerPath);
    }

    /// <summary>Recursive listing (<c>/path (N bytes)</c> lines + totals).</summary>
    public (int ExitCode, string StdOut, string StdErr) Tree(string imagePath)
    {
        return Run("tree", imagePath);
    }

    /// <summary>Version line for reports.</summary>
    public string GetVersion()
    {
        try
        {
            (var code, var so, var se) = Run("-V");
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
