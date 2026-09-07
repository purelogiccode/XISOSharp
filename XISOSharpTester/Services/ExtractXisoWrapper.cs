using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;
using XISOSharpTester.Logging;

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

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

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
        return Run(CancellationToken.None, args);
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments and
    /// returns the captured result, observing cancellation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public Result Run(CancellationToken cancellationToken, params string[] args)
    {
        return RunAsync(args, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments and
    /// returns the captured result asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public async Task<Result> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(args);
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

            Log.Debug("Running extract-xiso: {Args}", string.Join(" ", args));
            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = new CancellationTokenSource(ProcessTimeout);
            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var ct = linkedCts.Token;

            using (ct.Register(static state =>
                   {
                       var proc = (Process)state!;
                       try
                       {
                           if (!proc.HasExited)
                           {
                               proc.Kill(entireProcessTree: true);
                           }
                       }
                       catch (Exception ex) when (ex is InvalidOperationException
                           or System.ComponentModel.Win32Exception
                           or NotSupportedException
                           or ObjectDisposedException)
                       {
                           // Already exited, disposed, or cannot kill — the wait below still completes.
                       }
                   }, process))
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                try
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"extract-xiso timed out after {ProcessTimeout.TotalSeconds:N0} seconds: {string.Join(" ", args)}");
                }

                string stdout;
                string stderr;
                try
                {
                    stdout = await stdoutTask.ConfigureAwait(false);
                    stderr = await stderrTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        $"extract-xiso timed out after {ProcessTimeout.TotalSeconds:N0} seconds: {string.Join(" ", args)}");
                }

                var exitCode = GetExitCodeSafe(process);
                var result = new Result { ExitCode = exitCode, StdOut = stdout, StdErr = stderr };
                if (result.ExitCode != 0)
                    Log.Warning("extract-xiso exited with code {Exit}: {Args}", result.ExitCode,
                        string.Join(" ", args));
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "extract-xiso run failed");
            BugReporter.ReportException(ex, "extract-xiso run failed");
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or ObjectDisposedException)
        {
            // Best effort — already exited or cannot kill.
        }
    }

    private static int GetExitCodeSafe(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments, appending
    /// the quiet flag (<c>-Q</c>) to suppress output.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public Result RunQuiet(params string[] args)
    {
        return RunQuiet(CancellationToken.None, args);
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments, appending
    /// the quiet flag (<c>-Q</c>) to suppress output, observing cancellation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public Result RunQuiet(CancellationToken cancellationToken, params string[] args)
    {
        return RunQuietAsync(args, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs extract-xiso.exe with the specified arguments, appending
    /// the quiet flag (<c>-Q</c>) to suppress output, asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing exit code and output.</returns>
    public Task<Result> RunQuietAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        return RunAsync([.. args, "-Q"], cancellationToken);
    }

    /// <summary>
    /// Lists the contents of an XISO image by invoking
    /// <c>extract-xiso -l &lt;isoPath&gt;</c>.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <returns>A <see cref="Result"/> containing the file listing.</returns>
    public Result ListFiles(string isoPath)
    {
        return ListFiles(isoPath, CancellationToken.None);
    }

    /// <summary>
    /// Lists the contents of an XISO image, observing cancellation.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing the file listing.</returns>
    public Result ListFiles(string isoPath, CancellationToken cancellationToken)
    {
        return ListFilesAsync(isoPath, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Lists the contents of an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing the file listing.</returns>
    public Task<Result> ListFilesAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        return RunAsync(["-l", isoPath], cancellationToken);
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
        return ExtractFiles(isoPath, outputDir, CancellationToken.None);
    }

    /// <summary>
    /// Extracts all files from an XISO image, observing cancellation.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to extract files into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing extraction output.</returns>
    public Result ExtractFiles(string isoPath, string outputDir, CancellationToken cancellationToken)
    {
        return ExtractFilesAsync(isoPath, outputDir, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Extracts all files from an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to extract files into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing extraction output.</returns>
    public Task<Result> ExtractFilesAsync(string isoPath, string outputDir,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(["-x", "-d", outputDir, isoPath], cancellationToken);
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
        return Rewrite(isoPath, outputDir, CancellationToken.None);
    }

    /// <summary>
    /// Rewrites (optimizes) an XISO image, observing cancellation.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to write the rewritten ISO into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing rewrite output.</returns>
    public Result Rewrite(string isoPath, string outputDir, CancellationToken cancellationToken)
    {
        return RewriteAsync(isoPath, outputDir, cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Rewrites (optimizes) an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to write the rewritten ISO into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="Result"/> containing rewrite output.</returns>
    public Task<Result> RewriteAsync(string isoPath, string outputDir,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(["-r", "-d", outputDir, isoPath], cancellationToken);
    }

    /// <summary>
    /// Retrieves the version string of extract-xiso.exe by running
    /// <c>extract-xiso -v</c> and parsing the first line of output.
    /// </summary>
    /// <returns>The version string, or <c>null</c> if unavailable.</returns>
    public string? GetVersion()
    {
        return GetVersion(CancellationToken.None);
    }

    /// <summary>
    /// Retrieves the version string of extract-xiso.exe, observing cancellation.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>The version string, or <c>null</c> if unavailable.</returns>
    public string? GetVersion(CancellationToken cancellationToken)
    {
        return GetVersionAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Retrieves the version string of extract-xiso.exe asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>The version string, or <c>null</c> if unavailable.</returns>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await RunAsync(["-v"], cancellationToken).ConfigureAwait(false);
            if (r.ExitCode != 0 && r.ExitCode != 255) return null;

            var stdout = r.StdOut.Trim();
            if (string.IsNullOrEmpty(stdout))
            {
                stdout = r.All.Trim();
            }

            var lines = stdout.Split('\n');
            return lines.FirstOrDefault()?.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetVersion failed");
            BugReporter.ReportException(ex, "GetVersion failed");
            return null;
        }
    }

    /// <summary>
    /// Releases all resources used by this wrapper instance.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
