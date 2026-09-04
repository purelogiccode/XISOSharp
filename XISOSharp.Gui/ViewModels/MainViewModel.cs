using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private CancellationTokenSource? runningCts;

    // Settings
    [ObservableProperty] public partial string CliPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string CliStatus { get; set; } = "CLI not located yet.";

    [ObservableProperty] public partial bool OverwriteExisting { get; set; }

    // Shared run state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsRunning { get; set; }

    public bool CanRun => !IsRunning;

    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;

    [ObservableProperty] public partial string LastExit { get; set; } = string.Empty;

    // Extract tab
    [ObservableProperty] public partial string ExImage { get; set; } = string.Empty;

    [ObservableProperty] public partial string ExDest { get; set; } = string.Empty;

    [ObservableProperty] public partial string ExInfoPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string ExCopyPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string ExCopyDest { get; set; } = string.Empty;

    // Create tab
    [ObservableProperty] public partial string CrSource { get; set; } = string.Empty;

    [ObservableProperty] public partial string CrName { get; set; } = string.Empty;

    [ObservableProperty] public partial string CrExcludes { get; set; } = string.Empty;

    [ObservableProperty] public partial bool CrSkipSystemUpdate { get; set; }

    [ObservableProperty] public partial bool CrDisableXbePatch { get; set; }

    // Rewrite tab (+ wipe/trim helpers)
    [ObservableProperty] public partial string RwImages { get; set; } = string.Empty;

    [ObservableProperty] public partial string RwOutput { get; set; } = string.Empty;

    [ObservableProperty] public partial string RwWorkDir { get; set; } = string.Empty;

    [ObservableProperty] public partial bool RwDeleteOld { get; set; }

    [ObservableProperty] public partial bool RwDisableXbePatch { get; set; }

    [ObservableProperty] public partial bool RwValidate { get; set; }

    [ObservableProperty] public partial bool RwChecksums { get; set; }

    [ObservableProperty] public partial bool RwStrict { get; set; }

    [ObservableProperty] public partial string RwReport { get; set; } = string.Empty;

    [ObservableProperty] public partial string WpImage { get; set; } = string.Empty;

    [ObservableProperty] public partial string WpOutput { get; set; } = string.Empty;

    // Rebuild tab
    [ObservableProperty] public partial string RbParts { get; set; } = string.Empty;

    [ObservableProperty] public partial string RbOutput { get; set; } = string.Empty;

    [ObservableProperty] public partial string RbSectors { get; set; } = string.Empty;

    // Compress tab
    [ObservableProperty] public partial string CpSource { get; set; } = string.Empty;

    [ObservableProperty] public partial string CpOutput { get; set; } = string.Empty;

    [ObservableProperty] public partial int CpLevel { get; set; } = 9;

    [ObservableProperty] public partial string CpVersion { get; set; } = "2";

    [ObservableProperty] public partial string CpSplit { get; set; } = string.Empty;

    // Decompress tab
    [ObservableProperty] public partial string DcCso { get; set; } = string.Empty;

    [ObservableProperty] public partial string DcOutput { get; set; } = string.Empty;

    // Validate tab (+ checksum group)
    [ObservableProperty] public partial string VaSource { get; set; } = string.Empty;

    [ObservableProperty] public partial string VaOutput { get; set; } = string.Empty;

    [ObservableProperty] public partial bool VaChecksums { get; set; }

    [ObservableProperty] public partial string VaReport { get; set; } = string.Empty;

    [ObservableProperty] public partial string CsImages { get; set; } = string.Empty;

    [ObservableProperty] public partial bool CsSilent { get; set; }

    // Batch tab
    [ObservableProperty] public partial string BaDir { get; set; } = string.Empty;

    [ObservableProperty] public partial bool BaRecursive { get; set; }

    [ObservableProperty] public partial string BaMode { get; set; } = "Extract";

    [ObservableProperty] public partial string BaDest { get; set; } = string.Empty;
    internal IReadOnlyList<string> BatchModes { get; } = ["Extract", "List", "Tree", "Rewrite", "Audit"];
    internal IReadOnlyList<string> CisoVersions { get; } = ["1", "2"];

    internal async Task InitializeAsync()
    {
        var settings = GuiSettings.Load();
        CliPath = settings.CliPath;
        OverwriteExisting = settings.OverwriteByDefault;
        await DetectCliAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DetectCliAsync()
    {
        var resolved = CliLocator.Resolve(string.IsNullOrWhiteSpace(CliPath) ? null : CliPath);
        if (resolved is null)
        {
            CliStatus = "XISOSharp CLI not found — set the CLI path on the Settings tab.";
            AppendLog("[GUI] XISOSharp CLI not found (override, app folder, or PATH).");
            return;
        }

        CliPath = resolved;
        AppendLog($"[GUI] Using CLI: {resolved}");
        var version = await CliLocator.ProbeVersionAsync(resolved, CancellationToken.None).ConfigureAwait(false);
        CliStatus = version is null ? $"Found but -v failed: {resolved}" : $"Ready — {version}";
        AppendLog(version is null ? "[GUI] CLI -v probe failed." : $"[GUI] {version}");
    }

    [RelayCommand]
    private void SaveSettings()
    {
        new GuiSettings { CliPath = CliPath, OverwriteByDefault = OverwriteExisting }.Save();
        AppendLog("[GUI] Settings saved.");
    }

    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;

    [RelayCommand]
    private void CancelRun()
    {
        try
        {
            runningCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Run already finished — nothing to cancel.
        }
    }

    [RelayCommand]
    private Task RunExtractAsync() => GuardedAsync(() => RunSingleImageAsync("extract", ExImage,
        CliCommands.Extract(RequireOne(ExImage, "image"), NullIfEmpty(ExDest), OverwriteExisting)));

    [RelayCommand]
    private Task RunListAsync() => GuardedAsync(() => RunSingleImageAsync("list", ExImage,
        CliCommands.List(RequireOne(ExImage, "image"))));

    [RelayCommand]
    private Task RunTreeAsync() => GuardedAsync(() => RunSingleImageAsync("tree", ExImage,
        CliCommands.Tree(RequireOne(ExImage, "image"))));

    [RelayCommand]
    private Task RunInfoAsync() => GuardedAsync(() => RunSingleImageAsync("info", ExImage,
        CliCommands.Info(RequireValue(ExImage, "image"), NullIfEmpty(ExInfoPath))));

    [RelayCommand]
    private Task RunUnpackAsync() => GuardedAsync(() => RunSingleImageAsync("unpack", ExImage,
        CliCommands.Unpack(RequireValue(ExImage, "image"), NullIfEmpty(ExDest))));

    [RelayCommand]
    private Task RunCopyOutAsync() => GuardedAsync(() => RunJobAsync("copy-out",
        CliCommands.CopyOut(RequireValue(ExImage, "image"), RequireValue(ExCopyPath, "in-image path"),
            RequireValue(ExCopyDest, "destination"))));

    [RelayCommand]
    private Task RunCreateAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(CrSource, "source directory");
        var excludes = SplitLines(CrExcludes);
        return RunJobAsync("create", CliCommands.Create(source, NullIfEmpty(CrName), excludes,
            CrSkipSystemUpdate, CrDisableXbePatch, OverwriteExisting));
    });

    [RelayCommand]
    private Task RunRewriteAsync() => GuardedAsync(() =>
    {
        var images = RequireLines(RwImages, "image");
        return RunJobAsync("rewrite", CliCommands.Rewrite(images, NullIfEmpty(RwOutput), NullIfEmpty(RwWorkDir),
            RwDeleteOld, RwDisableXbePatch, RwValidate, RwChecksums, RwStrict, NullIfEmpty(RwReport),
            OverwriteExisting));
    });

    [RelayCommand]
    private Task RunWipeAsync() => GuardedAsync(() => RunJobAsync("wipe",
        CliCommands.Wipe(RequireValue(WpImage, "image"), NullIfEmpty(WpOutput), OverwriteExisting)));

    [RelayCommand]
    private Task RunTrimAsync() => GuardedAsync(() => RunJobAsync("trim",
        CliCommands.Trim(RequireValue(WpImage, "image"), NullIfEmpty(WpOutput), OverwriteExisting)));

    [RelayCommand]
    private Task RunRebuildAsync() => GuardedAsync(() =>
    {
        var parts = RequireLines(RbParts, "component");
        var output = RequireValue(RbOutput, "output Redump ISO");
        return RunJobAsync("rebuild", CliCommands.Rebuild(parts, output, NullIfEmpty(RbSectors), OverwriteExisting));
    });

    [RelayCommand]
    private Task RunCompressAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(CpSource, "source directory or image");
        return RunJobAsync("compress", CliCommands.Compress(source, NullIfEmpty(CpOutput),
            Math.Clamp(CpLevel, 0, 9), string.Equals(CpVersion, "1", StringComparison.Ordinal) ? 1 : 2,
            NullIfEmpty(CpSplit), OverwriteExisting));
    });

    [RelayCommand]
    private Task RunDecompressAsync() => GuardedAsync(() => RunJobAsync("decompress",
        CliCommands.Decompress(RequireValue(DcCso, "CSO file"), NullIfEmpty(DcOutput), OverwriteExisting)));

    [RelayCommand]
    private Task RunValidateAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(VaSource, "source ISO");
        var output = RequireValue(VaOutput, "output ISO");
        return RunJobAsync("validate", CliCommands.Validate(source, output, VaChecksums, NullIfEmpty(VaReport)));
    });

    [RelayCommand]
    private Task RunChecksumAsync() => GuardedAsync(() =>
    {
        var images = RequireLines(string.IsNullOrWhiteSpace(CsImages) ? VaSource : CsImages, "image");
        return RunJobAsync("checksum", CliCommands.Checksum(images, CsSilent));
    });

    [RelayCommand]
    private Task RunBatchAsync() => GuardedAsync(() =>
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
            AppendLog($"[GUI] {ex.Message}");
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

        var cli = CliLocator.Resolve(string.IsNullOrWhiteSpace(CliPath) ? null : CliPath);
        if (cli is null)
        {
            AppendLog("[GUI] XISOSharp CLI not found — set the CLI path on the Settings tab.");
            return;
        }

        IsRunning = true;
        LastExit = string.Empty;
        using var cts = new CancellationTokenSource();
        runningCts = cts;
        try
        {
            AppendLog($"$ XISOSharp.Cli {Quote(args)}");
            var exit = await CliRunner.RunAsync(cli, args, AppendLog, cts.Token).ConfigureAwait(false);
            LastExit = $"Exit code: {exit}";
            AppendLog($"[GUI] {title} finished with exit code {exit}.");
        }
        finally
        {
            runningCts = null;
            IsRunning = false;
        }
    }

    private void AppendLog(string line)
    {
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

    private static string Quote(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

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

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}