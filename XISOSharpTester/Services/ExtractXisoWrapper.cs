using System.Diagnostics;
using System.IO;
using System.Text;

#pragma warning disable MA0048 // File name must match type name — class name intentionally differs from file name

namespace XISOSharpTester.Services;

/// <summary>
/// Wraps the extract-xiso.exe command-line tool, providing
/// managed methods for listing, extracting, and rewriting
/// XISO disc images. Implements <see cref="IDisposable"/>
/// to allow deterministic cleanup.
/// </summary>
public class XisoSharpWrapper : IDisposable
{
    private readonly string _exePath;

    /// <summary>
    /// Initializes a new instance of <see cref="XisoSharpWrapper"/>
    /// with the path to the extract-xiso executable.
    /// </summary>
    /// <param name="exePath">Full path to extract-xiso.exe.</param>
    public XisoSharpWrapper(string exePath)
    {
        _exePath = exePath;
    }

    /// <summary>
    /// Gets whether the configured extract-xiso executable exists
    /// on disk and is available for use.
    /// </summary>
    public bool Available => File.Exists(_exePath);

    /// <summary>
    /// Holds the result of a single extract-xiso process execution,
    /// including exit code and captured standard output/error.
    /// </summary>
    public sealed class Result
    {
        internal int ExitCode;
        internal string StdOut = null!;
        internal string StdErr = null!;
        internal string All => StdOut + "\n" + StdErr;
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments and
    /// returns the captured result.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
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

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments, appending
    /// the quiet flag (<c>-Q</c>) to suppress output.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public Result RunQuiet(params string[] args)
    {
        return Run([.. args, "-Q"]);
    }

    /// <summary>
    /// Lists the contents of an XISO image by invoking
    /// <c>extract-xiso -l &lt;isoPath&gt;</c>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>A <see cref="Result"/> containing the file listing.</returns>
    public Result ListFiles(string isoPath)
    {
        return Run("-l", isoPath);
    }

    /// <summary>
    /// Extracts all files from an XISO image to the specified
    /// output directory by invoking <c>extract-xiso -x -d &lt;dir&gt; &lt;isoPath&gt;</c>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to extract files into.</param>
    /// <returns>A <see cref="Result"/> containing extraction output.</returns>
    public Result ExtractFiles(string isoPath, string outputDir)
    {
        return Run("-x", "-d", outputDir, isoPath);
    }

    /// <summary>
    /// Rewrites (optimizes) an XISO image by invoking
    /// <c>extract-xiso -r -d &lt;dir&gt; &lt;isoPath&gt;</c>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to write the rewritten ISO into.</param>
    /// <returns>A <see cref="Result"/> containing rewrite output.</returns>
    public Result Rewrite(string isoPath, string outputDir)
    {
        return Run("-r", "-d", outputDir, isoPath);
    }

    /// <summary>
    /// Retrieves the version string of extract-xiso.exe by running
    /// <c>extract-xiso -v</c> and parsing the first line of output.
    /// </summary>
    /// <returns>The version string, or <c>null</c> if unavailable.</returns>
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

    /// <summary>
    /// Releases all resources used by this wrapper instance.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}