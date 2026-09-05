using System.Diagnostics;
using System.Globalization;

namespace ZARSharp.Pipeline;

/// <summary>
/// External-tool runner, porting <c>core.py::_run_cmd</c> (ZarManager 1.2.0):
/// merged stdout/stderr scanned for <c>(\d+)%</c> progress at most every
/// 100 ms, exit <c>0</c>/<c>1</c> accepted (the latter is 7z's harmless
/// warning), anything else raising with the last output line attached,
/// cancellation killing the process. This is the seam a future GUI uses to
/// drive 7z / extract-xiso stages; ZAR pack/extract itself runs in-process
/// via <see cref="ZarPipeline"/>.
/// </summary>
public static partial class ProcessRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Runs <paramref name="fileName"/> and returns its exit code plus last output line.</summary>
    /// <exception cref="FileNotFoundException">When the tool is missing or blocked (ZarManager's AV_BLOCK).</exception>
    /// <exception cref="UnauthorizedAccessException">On Windows elevation error 740.</exception>
    /// <exception cref="InvalidOperationException">On nonzero (non-1) exit.</exception>
    public static ProcessResult Run(
        string fileName,
        string arguments = "",
        string? workingDirectory = null,
        IProgress<double>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new FileNotFoundException($"Required tool did not start: {fileName}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new UnauthorizedAccessException(
                $"Required tool needs elevation (run as administrator): {fileName}", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"Required tool not found or blocked by antivirus (allow-list it and retry): {fileName} ({ex.Message})", ex);
        }

        string? lastLine = null;
        try
        {
            lastLine = Pump(process, progress, pause, cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        ThrowIfFailed(process.ExitCode, lastLine, fileName);
        return new ProcessResult(process.ExitCode, lastLine);
    }

    private static string? Pump(
        Process process, IProgress<double>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        // Like core.py's stdout.read(1) loop: per-char so carriage-return
        // progress bars yield one line each. Stderr drains on events (every
        // known tool reports progress on stdout); its last line is kept for
        // failure messages, mirroring the merged stream's last_line.
        string? lastErr = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lastErr = e.Data;
            }
        };
        process.BeginErrorReadLine();

        var line = new System.Text.StringBuilder();
        string? lastOut = null;
        var clock = Stopwatch.StartNew();
        var stdout = process.StandardOutput;
        while (true)
        {
            pause.WaitIfPaused(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var ch = stdout.Read();
            if (ch == -1)
            {
                break;
            }

            if (ch == '\r' || ch == '\n')
            {
                if (line.Length > 0)
                {
                    lastOut = line.ToString();
                    line.Clear();
                    if (TryParseProgressLine(lastOut) is { } ratio &&
                        (clock.Elapsed >= ProgressInterval || ratio >= 1.0))
                    {
                        progress?.Report(ratio);
                        clock.Restart();
                    }
                }
            }
            else
            {
                line.Append((char)ch);
            }
        }

        if (line.Length > 0)
        {
            lastOut = line.ToString();
            if (TryParseProgressLine(lastOut) is { } finalRatio)
            {
                progress?.Report(finalRatio);
            }
        }

        return lastOut ?? lastErr;
    }

    /// <summary>
    /// Parses a <c>(\d+)%</c> progress line to 0..1 (first match wins), else
    /// null. Same leftmost semantics as <c>core.py</c>'s
    /// <c>PROGRESS_PATTERN</c>; hand-rolled (no regex backtracking surface).
    /// </summary>
    internal static double? TryParseProgressLine(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (!char.IsAsciiDigit(line[i]))
            {
                continue;
            }

            var j = i;
            while (j < line.Length && char.IsAsciiDigit(line[j]))
            {
                j++;
            }

            if (j < line.Length && line[j] == '%' &&
                int.TryParse(line.AsSpan(i, j - i), NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            {
                return Math.Clamp(percent / 100.0, 0.0, 1.0);
            }

            i = j;
        }

        return null;
    }

    /// <summary>Accepts exit 0/1, else throws with the last line attached (ports <c>_run_cmd</c>).</summary>
    /// <exception cref="InvalidOperationException">On nonzero (non-1) exit.</exception>
    internal static void ThrowIfFailed(int exitCode, string? lastLine, string fileName)
    {
        if (exitCode is 0 or 1)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tool failed with exit code {exitCode}: {fileName} ({lastLine ?? "no output"})");
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            /* best effort */
        }
    }

    /// <summary>Exit code plus the last output line of a finished tool.</summary>
    public sealed record ProcessResult(int ExitCode, string? LastLine);
}
