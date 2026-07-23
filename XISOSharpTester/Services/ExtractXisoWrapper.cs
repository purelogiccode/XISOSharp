using System.Diagnostics;
using System.IO;
using System.Text;

namespace XISOSharpTester.Services;

public class XisoSharpWrapper : IDisposable
{
    private readonly string _exePath;

    public XisoSharpWrapper(string exePath)
    {
        _exePath = exePath;
    }

    public bool Available => File.Exists(_exePath);

    public sealed class Result
    {
        internal int ExitCode;
        internal string StdOut = null!;
        internal string StdErr = null!;
        internal string All => StdOut + "\n" + StdErr;
    }

    public Result Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var tOut = p.StandardOutput.ReadToEndAsync();
        var tErr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new Result { ExitCode = p.ExitCode, StdOut = tOut.Result, StdErr = tErr.Result };
    }

    public Result RunQuiet(params string[] args)
    {
        return Run([.. args, "-Q"]);
    }

    public Result ListFiles(string isoPath)
    {
        return Run("-l", isoPath);
    }

    public Result ExtractFiles(string isoPath, string outputDir)
    {
        return Run("-x", "-d", outputDir, isoPath);
    }

    public Result Rewrite(string isoPath, string outputDir)
    {
        return Run("-r", "-d", outputDir, isoPath);
    }

    public string? GetVersion()
    {
        var r = Run("-v");
        if (r.ExitCode != 0 && r.ExitCode != 255) return null;

        var stdout = r.StdOut.Trim();
        if (string.IsNullOrEmpty(stdout))
        {
            stdout = r.All.Trim();
        }

        var lines = stdout.Split('\n');
        return lines.FirstOrDefault()?.Trim();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
