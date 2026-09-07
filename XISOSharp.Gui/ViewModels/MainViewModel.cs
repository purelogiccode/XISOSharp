using System.Text;
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
    // Bounded log: keeps the UI responsive while retaining batch-run evidence.
    // 5000 lines (~500 KB) stays smooth in the Avalonia TextBox; truncation
    // drops from the head only so the most recent errors remain visible.
    private const int MaxLogLines = 5000;
    private readonly Queue<string> _logLines = new();
    private readonly Lock _runningCtsLock = new();
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
    [NotifyCanExecuteChangedFor(nameof(RunExtractCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunListCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunUnpackCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCopyOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunRewriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunWipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTrimCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunRebuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCompressCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunDecompressCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunValidateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunChecksumCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Gets whether a new job can start (i.e. none is running).</summary>
    public bool CanRun => !IsRunning;

    /// <summary>Gets or sets the streamed CLI output log (capped, head-truncated).</summary>
    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    /// <summary>Gets or sets the last CLI exit-code line.</summary>
    [ObservableProperty]
    public partial string LastExit { get; set; } = string.Empty;

    // Extract tab
    /// <summary>Gets or sets the extract/list/tree/unpack image path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunExtractCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunListCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunUnpackCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCopyOutCommand))]
    public partial string ExImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional extract/unpack destination directory.</summary>
    [ObservableProperty]
    public partial string ExDest { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional in-image path for the info command.</summary>
    [ObservableProperty]
    public partial string ExInfoPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the in-image source path for copy-out.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCopyOutCommand))]
    public partial string ExCopyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the on-disk destination for copy-out.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCopyOutCommand))]
    public partial string ExCopyDest { get; set; } = string.Empty;

    // Create tab
    /// <summary>Gets or sets the source directory to pack into a new image.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCreateCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(RunRewriteCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(RunWipeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTrimCommand))]
    public partial string WpImage { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional wipe/trim output path.</summary>
    [ObservableProperty]
    public partial string WpOutput { get; set; } = string.Empty;

    // Rebuild tab
    /// <summary>Gets or sets the newline-separated Redump component paths for rebuild.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRebuildCommand))]
    public partial string RbParts { get; set; } = string.Empty;

    /// <summary>Gets or sets the rebuild output Redump ISO path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRebuildCommand))]
    public partial string RbOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional rebuild <c>--security-sectors</c> file path.</summary>
    [ObservableProperty]
    public partial string RbSectors { get; set; } = string.Empty;

    // Compress tab
    /// <summary>Gets or sets the compress source image or directory.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCompressCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(RunDecompressCommand))]
    public partial string DcCso { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional decompressed ISO output path.</summary>
    [ObservableProperty]
    public partial string DcOutput { get; set; } = string.Empty;

    // Validate tab (+ checksum group)
    /// <summary>Gets or sets the validate source ISO path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunValidateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunChecksumCommand))]
    public partial string VaSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the validate output ISO path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunValidateCommand))]
    public partial string VaOutput { get; set; } = string.Empty;

    /// <summary>Gets or sets whether validate passes <c>--validate-checksums</c>.</summary>
    [ObservableProperty]
    public partial bool VaChecksums { get; set; }

    /// <summary>Gets or sets the optional validate <c>--validate-report</c> path.</summary>
    [ObservableProperty]
    public partial string VaReport { get; set; } = string.Empty;

    /// <summary>Gets or sets the newline-separated image list for checksum (falls back to validate source).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunChecksumCommand))]
    public partial string CsImages { get; set; } = string.Empty;

    /// <summary>Gets or sets whether checksum passes <c>--silent</c>.</summary>
    [ObservableProperty]
    public partial bool CsSilent { get; set; }

    // Batch tab
    /// <summary>Gets or sets the batch scan directory.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBatchCommand))]
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
            var status = version is null ? $"Found but -v failed: {resolved}" : $"Ready — {version}";
            SetOnUi(() => CliStatus = status);
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
        _logLines.Clear();
        LogText = string.Empty;
    }

    /// <summary>Logs a GUI-originated message (e.g. drag-and-drop routing).</summary>
    internal void LogMessage(string line) => AppendLog(line);

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        CancellationTokenSource? snapshot;
        lock (_runningCtsLock)
        {
            snapshot = _runningCts;
        }

        try
        {
            snapshot?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run already finished — nothing to cancel.
        }
    }

    private bool CanCancelRun() => IsRunning;

    private bool CanRunExtract() => !IsRunning && !string.IsNullOrWhiteSpace(ExImage);

    private bool CanRunList() => !IsRunning && !string.IsNullOrWhiteSpace(ExImage);

    private bool CanRunTree() => !IsRunning && !string.IsNullOrWhiteSpace(ExImage);

    private bool CanRunInfo() => !IsRunning && !string.IsNullOrWhiteSpace(ExImage);

    private bool CanRunUnpack() => !IsRunning && !string.IsNullOrWhiteSpace(ExImage);

    private bool CanRunCopyOut() =>
        !IsRunning && !string.IsNullOrWhiteSpace(ExImage)
                   && !string.IsNullOrWhiteSpace(ExCopyPath) && !string.IsNullOrWhiteSpace(ExCopyDest);

    private bool CanRunCreate() => !IsRunning && !string.IsNullOrWhiteSpace(CrSource);

    private bool CanRunRewrite() => !IsRunning && !string.IsNullOrWhiteSpace(RwImages);

    private bool CanRunWipe() => !IsRunning && !string.IsNullOrWhiteSpace(WpImage);

    private bool CanRunTrim() => !IsRunning && !string.IsNullOrWhiteSpace(WpImage);

    private bool CanRunRebuild() => !IsRunning && !string.IsNullOrWhiteSpace(RbParts) && !string.IsNullOrWhiteSpace(RbOutput);

    private bool CanRunCompress() => !IsRunning && !string.IsNullOrWhiteSpace(CpSource);

    private bool CanRunDecompress() => !IsRunning && !string.IsNullOrWhiteSpace(DcCso);

    private bool CanRunValidate() => !IsRunning && !string.IsNullOrWhiteSpace(VaSource) && !string.IsNullOrWhiteSpace(VaOutput);

    private bool CanRunChecksum() => !IsRunning && (!string.IsNullOrWhiteSpace(CsImages) || !string.IsNullOrWhiteSpace(VaSource));

    private bool CanRunBatch() => !IsRunning && !string.IsNullOrWhiteSpace(BaDir);

    [RelayCommand(CanExecute = nameof(CanRunExtract))]
    private Task RunExtractAsync() =>
        GuardedAsync(() => RunSingleImageAsync("extract", ExImage,
            CliCommands.Extract(RequireOne(ExImage, "image"), NullIfEmpty(ExDest), OverwriteExisting)));

    [RelayCommand(CanExecute = nameof(CanRunList))]
    private Task RunListAsync() =>
        GuardedAsync(() => RunSingleImageAsync("list", ExImage,
            CliCommands.List(RequireOne(ExImage, "image"))));

    [RelayCommand(CanExecute = nameof(CanRunTree))]
    private Task RunTreeAsync() =>
        GuardedAsync(() => RunSingleImageAsync("tree", ExImage,
            CliCommands.Tree(RequireOne(ExImage, "image"))));

    [RelayCommand(CanExecute = nameof(CanRunInfo))]
    private Task RunInfoAsync() =>
        GuardedAsync(() => RunSingleImageAsync("info", ExImage,
            CliCommands.Info(RequireValue(ExImage, "image"), NullIfEmpty(ExInfoPath))));

    [RelayCommand(CanExecute = nameof(CanRunUnpack))]
    private Task RunUnpackAsync() =>
        GuardedAsync(() => RunSingleImageAsync("unpack", ExImage,
            CliCommands.Unpack(RequireValue(ExImage, "image"), NullIfEmpty(ExDest))));

    [RelayCommand(CanExecute = nameof(CanRunCopyOut))]
    private Task RunCopyOutAsync() =>
        GuardedAsync(() => RunJobAsync("copy-out",
            CliCommands.CopyOut(RequireValue(ExImage, "image"), RequireValue(ExCopyPath, "in-image path"),
                RequireValue(ExCopyDest, "destination"))));

    [RelayCommand(CanExecute = nameof(CanRunCreate))]
    private Task RunCreateAsync() =>
        GuardedAsync(() =>
        {
            var source = RequireValue(CrSource, "source directory");
            var excludes = SplitLines(CrExcludes);
            return RunJobAsync("create", CliCommands.Create(source, NullIfEmpty(CrName), excludes,
                CrSkipSystemUpdate, CrDisableXbePatch, OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunRewrite))]
    private Task RunRewriteAsync() =>
        GuardedAsync(() =>
        {
            var images = RequireLines(RwImages, "image");
            ThrowIfRewriteCollision(NullIfEmpty(RwOutput), images);
            return RunJobAsync("rewrite", CliCommands.Rewrite(images, NullIfEmpty(RwOutput), NullIfEmpty(RwWorkDir),
                RwDeleteOld, RwDisableXbePatch, RwValidate, RwChecksums, RwStrict, NullIfEmpty(RwReport),
                OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunWipe))]
    private Task RunWipeAsync() =>
        GuardedAsync(() =>
        {
            var image = RequireValue(WpImage, "image");
            ThrowIfSameOutput(NullIfEmpty(WpOutput), [image], "Wipe output");
            return RunJobAsync("wipe",
                CliCommands.Wipe(image, NullIfEmpty(WpOutput), OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunTrim))]
    private Task RunTrimAsync() =>
        GuardedAsync(() =>
        {
            var image = RequireValue(WpImage, "image");
            ThrowIfSameOutput(NullIfEmpty(WpOutput), [image], "Trim output");
            return RunJobAsync("trim",
                CliCommands.Trim(image, NullIfEmpty(WpOutput), OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunRebuild))]
    private Task RunRebuildAsync() =>
        GuardedAsync(() =>
        {
            var parts = RequireLines(RbParts, "component");
            var output = RequireValue(RbOutput, "output Redump ISO");
            ThrowIfSameOutput(output, string.IsNullOrWhiteSpace(RbSectors) ? parts : [.. parts, RbSectors.Trim()],
                "Rebuild output");
            return RunJobAsync("rebuild",
                CliCommands.Rebuild(parts, output, NullIfEmpty(RbSectors), OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunCompress))]
    private Task RunCompressAsync() =>
        GuardedAsync(() =>
        {
            var source = RequireValue(CpSource, "source directory or image");
            ThrowIfCompressCollision(source, NullIfEmpty(CpOutput), NullIfEmpty(CpSplit));
            return RunJobAsync("compress", CliCommands.Compress(source, NullIfEmpty(CpOutput),
                Math.Clamp(CpLevel, 0, 9), string.Equals(CpVersion, "1", StringComparison.Ordinal) ? 1 : 2,
                NullIfEmpty(CpSplit), OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunDecompress))]
    private Task RunDecompressAsync() =>
        GuardedAsync(() =>
        {
            var cso = RequireValue(DcCso, "CSO file");
            ThrowIfDecompressCollision(cso, NullIfEmpty(DcOutput));
            return RunJobAsync("decompress",
                CliCommands.Decompress(cso, NullIfEmpty(DcOutput), OverwriteExisting));
        });

    [RelayCommand(CanExecute = nameof(CanRunValidate))]
    private Task RunValidateAsync() =>
        GuardedAsync(() =>
        {
            var source = RequireValue(VaSource, "source ISO");
            var output = RequireValue(VaOutput, "output ISO");
            return RunJobAsync("validate", CliCommands.Validate(source, output, VaChecksums, NullIfEmpty(VaReport)));
        });

    [RelayCommand(CanExecute = nameof(CanRunChecksum))]
    private Task RunChecksumAsync() =>
        GuardedAsync(() =>
        {
            var images = RequireLines(string.IsNullOrWhiteSpace(CsImages) ? VaSource : CsImages, "image");
            return RunJobAsync("checksum", CliCommands.Checksum(images, CsSilent));
        });

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private Task RunBatchAsync() =>
        GuardedAsync(() =>
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
    /// Fail-closed like <c>CliOutputGuard</c>: an unverifiable comparison blocks
    /// the run instead of waving it through.
    /// </summary>
    private static void ThrowIfSameOutput(string? output, IEnumerable<string> inputs, string what)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            foreach (var input in inputs)
            {
                if (XisoPaths.AreSamePath(output, input))
                {
                    throw new InvalidOperationException(
                        $"{what} is the same file as the input ({input}); choose another output.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{what} could not be verified against its inputs ({ex.Message}); refusing to overwrite.");
        }
    }

    /// <summary>
    /// Rewrite guard mirroring <c>CliOutputGuard.CheckRewriteOutput</c> per input:
    /// an <c>-o</c> pointing at any input or at the <c>.old</c> backup about to
    /// hold it is refused. A <c>null</c> output means in-place rewrite rules.
    /// </summary>
    private static void ThrowIfRewriteCollision(string? output, IReadOnlyList<string> images)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            var trimmed = output.Trim();
            foreach (var raw in images)
            {
                var input = raw.Trim();
                if (XisoPaths.AreSamePath(trimmed, input))
                {
                    throw new InvalidOperationException(
                        $"Rewrite output is the same file as the input ({input}); omit -o to rewrite in place.");
                }

                if (XisoPaths.AreSamePath(trimmed, input + ".old"))
                {
                    throw new InvalidOperationException(
                        $"Rewrite output would overwrite the {input}.old backup; choose another name.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Rewrite output could not be verified ({ex.Message}); refusing to overwrite.");
        }
    }

    /// <summary>
    /// Compress guard mirroring the CLI compress path: the (explicit or derived)
    /// base output must not equal the source, and when splitting the derived
    /// first-part probe (<c>.1.cso</c>) must not equal the source either.
    /// </summary>
    private static void ThrowIfCompressCollision(string source, string? output, string? splitBytes)
    {
        try
        {
            var src = source.Trim();
            string outputBase;
            if (string.IsNullOrWhiteSpace(output))
            {
                bool isDir;
                try
                {
                    isDir = Directory.Exists(src);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Could not verify compress output against {src} ({ex.Message}); refusing to overwrite.");
                }

                try
                {
                    outputBase = CisoWriter.DeriveDefaultCsoPath(src, isDir);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not verify compress output against {src} ({ex.Message}); refusing to overwrite.");
                }
            }
            else
            {
                outputBase = output.Trim();
            }

            if (XisoPaths.AreSamePath(src, outputBase))
            {
                throw new InvalidOperationException(
                    $"Compress output is the same file as the input ({src}); choose another output.");
            }

            var splitting = !string.IsNullOrWhiteSpace(splitBytes)
                            && !string.Equals(splitBytes.Trim(), "0", StringComparison.Ordinal);
            if (splitting)
            {
                string firstPart;
                try
                {
                    firstPart = Path.ChangeExtension(outputBase, "1.cso");
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw new InvalidOperationException(
                        $"Could not verify compress split output {outputBase} ({ex.Message}); refusing to overwrite.");
                }

                if (firstPart is not null && XisoPaths.AreSamePath(src, firstPart))
                {
                    throw new InvalidOperationException(
                        $"Compress output part {firstPart} is the same file as the input ({src}); choose another output.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Compress output could not be verified ({ex.Message}); refusing to overwrite.");
        }
    }

    /// <summary>
    /// Decompress guard mirroring the CLI decompress path: the (explicit or
    /// derived) <c>.iso</c> output must not equal the source CSO.
    /// </summary>
    private static void ThrowIfDecompressCollision(string cso, string? output)
    {
        try
        {
            var src = cso.Trim();
            string probe;
            if (string.IsNullOrWhiteSpace(output))
            {
                try
                {
                    probe = CisoReader.DeriveDefaultIsoPath(src);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not verify decompress output against {src} ({ex.Message}); refusing to overwrite.");
                }
            }
            else
            {
                probe = output.Trim();
            }

            if (XisoPaths.AreSamePath(src, probe))
            {
                throw new InvalidOperationException(
                    $"Decompress output is the same file as the input ({src}); choose another output.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Decompress output could not be verified ({ex.Message}); refusing to overwrite.");
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
        var cts = new CancellationTokenSource();
        lock (_runningCtsLock)
        {
            _runningCts = cts;
        }

        try
        {
            AppendLog($"$ XISOSharp.Cli {Quote(args)}");
            Log.Information("Starting {Title}: XISOSharp.Cli {Args}", title, Quote(args));
            var exit = await CliRunner.RunAsync(cli, args, AppendLog, cts.Token).ConfigureAwait(false);
            var exitText = $"Exit code: {exit}";
            SetOnUi(() => LastExit = exitText);
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
            lock (_runningCtsLock)
            {
                if (ReferenceEquals(_runningCts, cts))
                {
                    _runningCts = null;
                }
            }

            cts.Dispose();
            SetOnUi(() => IsRunning = false);
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

        SetOnUi(() => AppendLogCore(line));
    }

    /// <summary>
    /// Runs <paramref name="update"/> on the Avalonia UI thread. Observable
    /// properties must only change on the UI thread — several flows above
    /// hop to the pool with <c>ConfigureAwait(false)</c>, so every UI-bound
    /// set after an await goes through here.
    /// </summary>
    private static void SetOnUi(Action update)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                update();
            }
            else
            {
                Dispatcher.UIThread.Post(update);
            }
        }
        catch
        {
            // Dispatcher gone (shutdown) — dropping a UI refresh is safe.
        }
    }

    private void AppendLogCore(string line)
    {
        foreach (var part in line.Split('\n'))
        {
            var text = part.Length > 0 && part[^1] == '\r' ? part[..^1] : part;
            _logLines.Enqueue(text);
        }

        while (_logLines.Count > MaxLogLines)
        {
            _logLines.Dequeue();
        }

        var sb = new StringBuilder();
        foreach (var queued in _logLines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(queued);
        }

        LogText = sb.ToString();
    }

    private static string Quote(IReadOnlyList<string> args) => string.Join(" ", args.Select(QuoteOne));

    private static string QuoteOne(string arg)
    {
        if (arg.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('\t', StringComparison.Ordinal)
                                                                      || arg.Contains('"', StringComparison.Ordinal) ||
                                                                      arg.Contains('\n', StringComparison.Ordinal)
                                                                      || arg.Contains('\r', StringComparison.Ordinal);
        if (!needsQuotes)
        {
            return arg;
        }

        var escaped = arg.Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static List<string> RequireOne(string value, string what) => [RequireValue(value, what)];

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

    private static List<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
