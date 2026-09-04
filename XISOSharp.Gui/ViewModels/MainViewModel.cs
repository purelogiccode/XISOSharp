using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.Services;

namespace XISOSharp.Gui.ViewModels;

/// <summary>
/// Single view-model behind every GUI tab. Each action builds an argv via
/// <see cref="CliCommands"/> and runs it through <see cref="CliRunner"/>,
/// streaming CLI output into <see cref="LogText"/>.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 400;
    private CancellationTokenSource? _runningCts;

    // Settings
    /// <summary>Gets or sets the resolved CLI executable path.</summary>
    [ObservableProperty]
    public partial string CliPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the CLI readiness status line shown on the Settings tab.</summary>
    [ObservableProperty]
    public partial string CliStatus { get; set; } = "CLI not located yet.";

    /// <summary>Gets or sets whether commands pass <c>-y</c> (overwrite) instead of <c>-n</c>.</summary>
    [ObservableProperty]
    public partial bool OverwriteExisting { get; set; }

    // Shared run state
    /// <summary>Gets or sets whether a CLI job is currently running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsRunning { get; set; }

    /// <summary>Gets whether a new job can start (i.e. none is running).</summary>
    public bool CanRun => !IsRunning;

    /// <summary>Gets or sets the streamed CLI output log (capped at 400 lines).</summary>
    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    /// <summary>Gets or sets the last CLI exit-code line.</summary>
    [ObservableProperty]
    public partial string LastExit { get; set; } = string.Empty;

    // Extract tab
    /// <summary>Gets or sets the extract/list/tree/unpack image path.</summary>
    [ObservableProperty]
    public partial string ExImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional extract/unpack destination directory.</summary>
    [ObservableProperty]
    public partial string ExDest { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional in-image path for the info command.</summary>
    [ObservableProperty]
    public partial string ExInfoPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the in-image source path for copy-out.</summary>
    [ObservableProperty]
    public partial string ExCopyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the on-disk destination for copy-out.</summary>
    [ObservableProperty]
    public partial string ExCopyDest { get; set; } = string.Empty;

    // Create tab
    /// <summary>Gets or sets the source directory to pack into a new image.</summary>
    [ObservableProperty]
    public partial string CrSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional output name for the created image.</summary>
    [ObservableProperty]
    public partial string CrName { get; set; } = string.Empty;

    /// <summary>Gets or sets newline-separated exclude patterns (<c>-X</c>).</summary>
    [ObservableProperty]
    public partial string CrExcludes { get; set; } = string.Empty;

    /// <summary>Gets or sets whether to pass <c>-s</c> (skip system update).</summary>
    [ObservableProperty]
    public partial bool CrSkipSystemUpdate { get; set; }

    /// <summary>Gets or sets whether to pass <c>-m</c> (disable media/XBE patch).</summary>
    [ObservableProperty]
    public partial bool CrDisableXbePatch { get; set; }

    // Rewrite tab (+ wipe/trim helpers)
    /// <summary>Gets or sets the newline-separated image list for rewrite.</summary>
    [ObservableProperty]
    public partial string RwImages { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional rewrite <c>-o</c> output path.</summary>
    [ObservableProperty]
    public partial string RwOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional rewrite <c>-d</c> work directory.</summary>
    [ObservableProperty]
    public partial string RwWorkDir { get; set; } = string.Empty;

    /// <summary>Gets or sets whether to pass <c>-D</c> (delete .old backup).</summary>
    [ObservableProperty]
    public partial bool RwDeleteOld { get; set; }

    /// <summary>Gets or sets whether to pass <c>-m</c> (disable media patch) for rewrite.</summary>
    [ObservableProperty]
    public partial bool RwDisableXbePatch { get; set; }

    /// <summary>Gets or sets whether to pass <c>--validate</c> for rewrite.</summary>
    [ObservableProperty]
    public partial bool RwValidate { get; set; }

    /// <summary>Gets or sets whether to pass <c>--validate-checksums</c> for rewrite.</summary>
    [ObservableProperty]
    public partial bool RwChecksums { get; set; }

    /// <summary>Gets or sets whether to pass <c>--validate-strict</c> for rewrite.</summary>
    [ObservableProperty]
    public partial bool RwStrict { get; set; }

    /// <summary>Gets or sets the optional <c>--validate-report</c> path for rewrite.</summary>
    [ObservableProperty]
    public partial string RwReport { get; set; } = string.Empty;

    /// <summary>Gets or sets the wipe/trim source image path.</summary>
    [ObservableProperty]
    public partial string WpImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional wipe/trim output path.</summary>
    [ObservableProperty]
    public partial string WpOutput { get; set; } = string.Empty;

    // Rebuild tab
    /// <summary>Gets or sets the newline-separated Redump component paths for rebuild.</summary>
    [ObservableProperty]
    public partial string RbParts { get; set; } = string.Empty;

    /// <summary>Gets or sets the rebuild output Redump ISO path.</summary>
    [ObservableProperty]
    public partial string RbOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional rebuild <c>--security-sectors</c> file path.</summary>
    [ObservableProperty]
    public partial string RbSectors { get; set; } = string.Empty;

    // Compress tab
    /// <summary>Gets or sets the compress source image or directory.</summary>
    [ObservableProperty]
    public partial string CpSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional compress output path.</summary>
    [ObservableProperty]
    public partial string CpOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets the CISO compression level (0-9).</summary>
    [ObservableProperty]
    public partial int CpLevel { get; set; } = 9;

    /// <summary>Gets or sets the CISO version ("1" or "2").</summary>
    [ObservableProperty]
    public partial string CpVersion { get; set; } = "2";

    /// <summary>Gets or sets the optional <c>--ciso-split</c> value.</summary>
    [ObservableProperty]
    public partial string CpSplit { get; set; } = string.Empty;

    // Decompress tab
    /// <summary>Gets or sets the source CSO path for decompression.</summary>
    [ObservableProperty]
    public partial string DcCso { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional decompressed ISO output path.</summary>
    [ObservableProperty]
    public partial string DcOutput { get; set; } = string.Empty;

    // Validate tab (+ checksum group)
    /// <summary>Gets or sets the validate source ISO path.</summary>
    [ObservableProperty]
    public partial string VaSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the validate output ISO path.</summary>
    [ObservableProperty]
    public partial string VaOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets whether validate passes <c>--validate-checksums</c>.</summary>
    [ObservableProperty]
    public partial bool VaChecksums { get; set; }

    /// <summary>Gets or sets the optional validate <c>--validate-report</c> path.</summary>
    [ObservableProperty]
    public partial string VaReport { get; set; } = string.Empty;

    /// <summary>Gets or sets the newline-separated image list for checksum (falls back to validate source).</summary>
    [ObservableProperty]
    public partial string CsImages { get; set; } = string.Empty;

    /// <summary>Gets or sets whether checksum passes <c>--silent</c>.</summary>
    [ObservableProperty]
    public partial bool CsSilent { get; set; }

    // Batch tab
    /// <summary>Gets or sets the batch scan directory.</summary>
    [ObservableProperty]
    public partial string BaDir { get; set; } = string.Empty;

    /// <summary>Gets or sets whether batch scans recursively.</summary>
    [ObservableProperty]
    public partial bool BaRecursive { get; set; }

    /// <summary>Gets or sets the selected batch mode (Extract, List, Tree, Rewrite, Audit).</summary>
    [ObservableProperty]
    public partial string BaMode { get; set; } = "Extract";

    /// <summary>Gets or sets the optional batch <c>-d</c> destination directory.</summary>
    [ObservableProperty]
    public partial string BaDest { get; set; } = string.Empty;

    /// <summary>Gets the batch mode names offered in the UI.</summary>
    internal IReadOnlyList<string> BatchModes { get; } = ["Extract", "List", "Tree", "Rewrite", "Audit"];

    /// <summary>Gets the CISO version choices offered in the UI.</summary>
    internal IReadOnlyList<string> CisoVersions { get; } = ["1", "2"];

    /// <summary>
    /// Loads persisted settings and probes for the CLI at startup.
    /// </summary>
    internal async Task InitializeAsync()
    {
        try
        {
            var settings = GuiSettings.Load();
            CliPath = settings.CliPath;
            OverwriteExisting = settings.OverwriteByDefault;
            await DetectCliAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI InitializeAsync failed");
            BugReporter.ReportException(ex, "GUI InitializeAsync failed");
            AppendLog($"[GUI] Initialization failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DetectCliAsync()
    {
        try
        {
            var resolved = CliLocator.Resolve(string.IsNullOrWhiteSpace(CliPath) ? null : CliPath);
            if (resolved is null)
            {
                CliStatus = "XISOSharp CLI not found — set the CLI path on the Settings tab.";
                AppendLog("[GUI] XISOSharp CLI not found (override, app folder, or PATH).");
                Log.Warning("CLI not found (override, app folder, or PATH)");
                return;
            }

            CliPath = resolved;
            AppendLog($"[GUI] Using CLI: {resolved}");
            var version = await CliLocator.ProbeVersionAsync(resolved, CancellationToken.None).ConfigureAwait(false);
            CliStatus = version is null ? $"Found but -v failed: {resolved}" : $"Ready — {version}";
            AppendLog(version is null ? "[GUI] CLI -v probe failed." : $"[GUI] {version}");
            if (version is null)
            {
                Log.Warning("CLI -v probe failed for {Cli}", resolved);
                BugReporter.ReportWarning($"CLI -v probe failed for {resolved}");
            }
            else
            {
                Log.Information("CLI ready: {Cli} ({Version})", resolved, version);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DetectCliAsync failed");
            BugReporter.ReportException(ex, "DetectCliAsync failed");
            CliStatus = $"CLI detection failed: {ex.Message}";
            AppendLog($"[GUI] CLI detection failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            new GuiSettings { CliPath = CliPath, OverwriteByDefault = OverwriteExisting }.Save();
            AppendLog("[GUI] Settings saved.");
            Log.Information("GUI settings saved");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SaveSettings failed");
            BugReporter.ReportException(ex, "SaveSettings failed");
            AppendLog($"[GUI] Settings save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogText = string.Empty;
    }

    /// <summary>Logs a GUI-originated message (e.g. drag-and-drop routing).</summary>
    internal void LogMessage(string line)
    {
        AppendLog(line);
    }

    [RelayCommand]
    private void CancelRun()
    {
        try
        {
            _runningCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run already finished — nothing to cancel.
        }
    }

    [RelayCommand]
    private Task RunExtractAsync()
    {
        return GuardedAsync(() => RunSingleImageAsync("extract", ExImage,
            CliCommands.Extract(RequireOne(ExImage, "image"), NullIfEmpty(ExDest), OverwriteExisting)));
    }

    [RelayCommand]
    private Task RunListAsync()
    {
        return GuardedAsync(() => RunSingleImageAsync("list", ExImage,
            CliCommands.List(RequireOne(ExImage, "image"))));
    }

    [RelayCommand]
    private Task RunTreeAsync()
    {
        return GuardedAsync(() => RunSingleImageAsync("tree", ExImage,
            CliCommands.Tree(RequireOne(ExImage, "image"))));
    }

    [RelayCommand]
    private Task RunInfoAsync()
    {
        return GuardedAsync(() => RunSingleImageAsync("info", ExImage,
            CliCommands.Info(RequireValue(ExImage, "image"), NullIfEmpty(ExInfoPath))));
    }

    [RelayCommand]
    private Task RunUnpackAsync()
    {
        return GuardedAsync(() => RunSingleImageAsync("unpack", ExImage,
            CliCommands.Unpack(RequireValue(ExImage, "image"), NullIfEmpty(ExDest))));
    }

    [RelayCommand]
    private Task RunCopyOutAsync()
    {
        return GuardedAsync(() => RunJobAsync("copy-out",
            CliCommands.CopyOut(RequireValue(ExImage, "image"), RequireValue(ExCopyPath, "in-image path"),
                RequireValue(ExCopyDest, "destination"))));
    }

    [RelayCommand]
    private Task RunCreateAsync()
    {
        return GuardedAsync(() =>
        {
            var source = RequireValue(CrSource, "source directory");
            var excludes = SplitLines(CrExcludes);
            return RunJobAsync("create", CliCommands.Create(source, NullIfEmpty(CrName), excludes,
                CrSkipSystemUpdate, CrDisableXbePatch, OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunRewriteAsync()
    {
        return GuardedAsync(() =>
        {
            var images = RequireLines(RwImages, "image");
            ThrowIfSameOutput(NullIfEmpty(RwOutput),
                images.Concat(images.Select(i => i + ".old")), "Rewrite output");
            return RunJobAsync("rewrite", CliCommands.Rewrite(images, NullIfEmpty(RwOutput), NullIfEmpty(RwWorkDir),
                RwDeleteOld, RwDisableXbePatch, RwValidate, RwChecksums, RwStrict, NullIfEmpty(RwReport),
                OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunWipeAsync()
    {
        return GuardedAsync(() =>
        {
            var image = RequireValue(WpImage, "image");
            ThrowIfSameOutput(NullIfEmpty(WpOutput), [image], "Wipe output");
            return RunJobAsync("wipe",
                CliCommands.Wipe(image, NullIfEmpty(WpOutput), OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunTrimAsync()
    {
        return GuardedAsync(() =>
        {
            var image = RequireValue(WpImage, "image");
            ThrowIfSameOutput(NullIfEmpty(WpOutput), [image], "Trim output");
            return RunJobAsync("trim",
                CliCommands.Trim(image, NullIfEmpty(WpOutput), OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunRebuildAsync()
    {
        return GuardedAsync(() =>
        {
            var parts = RequireLines(RbParts, "component");
            var output = RequireValue(RbOutput, "output Redump ISO");
            ThrowIfSameOutput(output, string.IsNullOrWhiteSpace(RbSectors) ? parts : [.. parts, RbSectors.Trim()],
                "Rebuild output");
            return RunJobAsync("rebuild",
                CliCommands.Rebuild(parts, output, NullIfEmpty(RbSectors), OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunCompressAsync()
    {
        return GuardedAsync(() =>
        {
            var source = RequireValue(CpSource, "source directory or image");
            ThrowIfSameOutput(NullIfEmpty(CpOutput), [source], "Compress output");
            return RunJobAsync("compress", CliCommands.Compress(source, NullIfEmpty(CpOutput),
                Math.Clamp(CpLevel, 0, 9), string.Equals(CpVersion, "1", StringComparison.Ordinal) ? 1 : 2,
                NullIfEmpty(CpSplit), OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunDecompressAsync()
    {
        return GuardedAsync(() =>
        {
            var cso = RequireValue(DcCso, "CSO file");
            ThrowIfSameOutput(NullIfEmpty(DcOutput), [cso], "Decompress output");
            return RunJobAsync("decompress",
                CliCommands.Decompress(cso, NullIfEmpty(DcOutput), OverwriteExisting));
        });
    }

    [RelayCommand]
    private Task RunValidateAsync()
    {
        return GuardedAsync(() =>
        {
            var source = RequireValue(VaSource, "source ISO");
            var output = RequireValue(VaOutput, "output ISO");
            return RunJobAsync("validate", CliCommands.Validate(source, output, VaChecksums, NullIfEmpty(VaReport)));
        });
    }

    [RelayCommand]
    private Task RunChecksumAsync()
    {
        return GuardedAsync(() =>
        {
            var images = RequireLines(string.IsNullOrWhiteSpace(CsImages) ? VaSource : CsImages, "image");
            return RunJobAsync("checksum", CliCommands.Checksum(images, CsSilent));
        });
    }

    [RelayCommand]
    private Task RunBatchAsync()
    {
        return GuardedAsync(() =>
        {
            var dir = RequireValue(BaDir, "batch directory");
            var modeFlag = BaMode switch
            {
                "List" => "-l",
                "Tree" => "-t",
                "Rewrite" => "-r",
                "Audit" => "-V",
                _ => "-x",
            };
            return RunJobAsync("batch",
                CliCommands.Batch(dir, BaRecursive, modeFlag, NullIfEmpty(BaDest), OverwriteExisting));
        });
    }

    private async Task GuardedAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "GUI validation: {Message}", ex.Message);
            AppendLog($"[GUI] {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI command failed");
            BugReporter.ReportException(ex, "GUI command failed");
            AppendLog($"[GUI] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Warn-before-run mirror of the CLI input==output guards (#15): refuses to
    /// start a job whose output would overwrite one of its inputs. Thrown before
    /// any CLI process spawns; <see cref="GuardedAsync"/> logs the message.
    /// A <c>null</c> output means "derive/default", which never collides.
    /// </summary>
    private static void ThrowIfSameOutput(string? output, IEnumerable<string> inputs, string what)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        foreach (var input in inputs)
        {
            if (XisoPaths.AreSamePath(output, input))
            {
                throw new InvalidOperationException(
                    $"{what} is the same file as the input ({input}); choose another output.");
            }
        }
    }

    private async Task RunSingleImageAsync(string title, string image, string[] args)
    {
        RequireOne(image, "image");
        await RunJobAsync(title, args).ConfigureAwait(false);
    }

    private async Task RunJobAsync(string title, string[] args)
    {
        if (IsRunning)
        {
            return;
        }

        string? cli;
        try
        {
            cli = CliLocator.Resolve(string.IsNullOrWhiteSpace(CliPath) ? null : CliPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI resolve failed for job {Title}", title);
            BugReporter.ReportException(ex, $"CLI resolve failed for job {title}");
            AppendLog($"[GUI] CLI resolve failed: {ex.Message}");
            return;
        }

        if (cli is null)
        {
            Log.Warning("Run {Title} refused: CLI not found", title);
            AppendLog("[GUI] XISOSharp CLI not found — set the CLI path on the Settings tab.");
            return;
        }

        IsRunning = true;
        LastExit = string.Empty;
        using var cts = new CancellationTokenSource();
        _runningCts = cts;
        try
        {
            AppendLog($"$ XISOSharp.Cli {Quote(args)}");
            Log.Information("Starting {Title}: XISOSharp.Cli {Args}", title, Quote(args));
            var exit = await CliRunner.RunAsync(cli, args, AppendLog, cts.Token).ConfigureAwait(false);
            LastExit = $"Exit code: {exit}";
            AppendLog($"[GUI] {title} finished with exit code {exit}.");
            if (exit != 0)
            {
                Log.Warning("Job {Title} exited with code {Exit}", title, exit);
                BugReporter.ReportWarning($"GUI job '{title}' exited with code {exit}: XISOSharp.Cli {Quote(args)}");
            }
            else
            {
                Log.Information("Job {Title} finished with exit code 0", title);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Job {Title} failed", title);
            BugReporter.ReportException(ex, $"GUI job '{title}' failed");
            AppendLog($"[GUI] {title} failed: {ex.Message}");
        }
        finally
        {
            _runningCts = null;
            IsRunning = false;
        }
    }

    private void AppendLog(string line)
    {
        try
        {
            Log.Information("{GuiLine}", line);
        }
        catch
        {
            // Serilog must never break UI logging.
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendLogCore(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendLogCore(line));
        }
    }

    private void AppendLogCore(string line)
    {
        var lines = LogText.Split('\n').ToList();
        lines.Add(line);
        while (lines.Count > MaxLogLines)
        {
            lines.RemoveAt(0);
        }

        LogText = string.Join('\n', lines);
    }

    private static string Quote(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
    }

    private static List<string> RequireOne(string value, string what)
    {
        return [RequireValue(value, what)];
    }

    private static string RequireValue(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Select {what} first.");
        }

        return value.Trim();
    }

    private static List<string> RequireLines(string value, string what)
    {
        var lines = SplitLines(value);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException($"Select {what} first.");
        }

        return lines;
    }

    private static List<string> SplitLines(string value)
    {
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}