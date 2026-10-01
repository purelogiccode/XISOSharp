using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOSharpTester.Models;

#pragma warning disable MA0048 // File name must match type name — class name intentionally differs from file name

namespace XISOSharpTester.Services;

/// <summary>
/// Wraps the extract-xiso command-line tool (extract-xiso.exe on Windows,
/// extensionless elsewhere), providing
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
    /// with the path to the extract-xiso executable (any OS spelling).
    /// </summary>
    /// <param name="exePath">Full path to the extract-xiso tool executable.</param>
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
    /// Runs the extract-xiso tool with the specified arguments and
    /// returns the captured result asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="XisoSharpResult"/> containing exit code and output.</returns>
    public async Task<XisoSharpResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(args);
            Log.Debug("Running extract-xiso: {Args}", string.Join(" ", args));
            // Thin delegate over the shared core runner (BUG-X-004): async drains,
            // timeout, cancel, tree-kill, ArgumentList, and exit reporting live in
            // XISOSharp.ProcessRunner so GUI and Tester stay identical.
            // extract-xiso prints raw Latin-1 bytes; decoding as UTF-8 would turn
            // non-ASCII names into U+FFFD and break list comparison.
            ProcessRunResult core = await ProcessRunner
                .RunAsync(_exePath, args, ProcessTimeout, cancellationToken,
                    outputEncoding: System.Text.Encoding.Latin1)
                .ConfigureAwait(false);
            XisoSharpResult result = new()
                { ExitCode = core.ExitCode, StdOut = core.StandardOutput, StdErr = core.StandardError };
            if (result.ExitCode != 0)
            {
                Log.Warning("extract-xiso exited with code {Exit}: {Args}", result.ExitCode,
                    string.Join(" ", args));
            }

            return result;
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
            throw;
        }
    }

    /// <summary>
    /// Runs the extract-xiso tool with the specified arguments, appending
    /// the quiet flag (<c>-Q</c>) to suppress output, asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments to pass.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="XisoSharpResult"/> containing exit code and output.</returns>
    public Task<XisoSharpResult> RunQuietAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        return RunAsync([.. args, "-Q"], cancellationToken);
    }

    /// <summary>
    /// Lists the contents of an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="XisoSharpResult"/> containing the file listing.</returns>
    public Task<XisoSharpResult> ListFilesAsync(string isoPath, CancellationToken cancellationToken = default) =>
        RunAsync(["-l", isoPath], cancellationToken);

    /// <summary>
    /// Extracts all files from an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to extract files into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="XisoSharpResult"/> containing extraction output.</returns>
    public Task<XisoSharpResult> ExtractFilesAsync(string isoPath, string outputDir,
        CancellationToken cancellationToken = default) =>
        RunAsync(["-x", "-d", outputDir, isoPath], cancellationToken);

    /// <summary>
    /// Rewrites (optimizes) an XISO image asynchronously.
    /// </summary>
    /// <param name="isoPath">Path to the XISO file.</param>
    /// <param name="outputDir">Directory to write the rewritten ISO into.</param>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>A <see cref="XisoSharpResult"/> containing rewrite output.</returns>
    public Task<XisoSharpResult> RewriteAsync(string isoPath, string outputDir,
        CancellationToken cancellationToken = default) =>
        RunAsync(["-r", "-d", outputDir, isoPath], cancellationToken);

    /// <summary>
    /// Retrieves the version string of the extract-xiso tool asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancels the run and kills the child process.</param>
    /// <returns>The version string, or <c>null</c> if unavailable.</returns>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            XisoSharpResult r = await RunAsync(["-v"], cancellationToken).ConfigureAwait(false);
            if (r.ExitCode != 0 && r.ExitCode != 255) return null;

            string stdout = r.StdOut.Trim();
            if (string.IsNullOrEmpty(stdout))
            {
                stdout = r.All.Trim();
            }

            string[] lines = stdout.Split('\n');
            return lines.FirstOrDefault()?.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetVersion failed");
            return null;
        }
    }

    /// <summary>
    /// Releases all resources used by this wrapper instance.
    /// </summary>
    public void Dispose() => GC.SuppressFinalize(this);
}
