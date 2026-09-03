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
    [ObservableProperty] private string cliPath = string.Empty;
    [ObservableProperty] private string cliStatus = "CLI not located yet.";
    [ObservableProperty] private bool overwriteExisting;

    // Shared run state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool isRunning;
    public bool CanRun => !IsRunning;

    [ObservableProperty] private string logText = string.Empty;
    [ObservableProperty] private string lastExit = string.Empty;

    // Extract tab
    [ObservableProperty] private string exImage = string.Empty;
    [ObservableProperty] private string exDest = string.Empty;
    [ObservableProperty] private string exInfoPath = string.Empty;
    [ObservableProperty] private string exCopyPath = string.Empty;
    [ObservableProperty] private string exCopyDest = string.Empty;

    // Create tab
    [ObservableProperty] private string crSource = string.Empty;
    [ObservableProperty] private string crName = string.Empty;
    [ObservableProperty] private string crExcludes = string.Empty;
    [ObservableProperty] private bool crSkipSystemUpdate;
    [ObservableProperty] private bool crDisableXbePatch;

    // Rewrite tab (+ wipe/trim helpers)
    [ObservableProperty] private string rwImages = string.Empty;
    [ObservableProperty] private string rwOutput = string.Empty;
    [ObservableProperty] private string rwWorkDir = string.Empty;
    [ObservableProperty] private bool rwDeleteOld;
    [ObservableProperty] private bool rwDisableXbePatch;
    [ObservableProperty] private bool rwValidate;
    [ObservableProperty] private bool rwChecksums;
    [ObservableProperty] private bool rwStrict;
    [ObservableProperty] private string rwReport = string.Empty;
    [ObservableProperty] private string wpImage = string.Empty;
    [ObservableProperty] private string wpOutput = string.Empty;

    // Rebuild tab
    [ObservableProperty] private string rbParts = string.Empty;
    [ObservableProperty] private string rbOutput = string.Empty;
    [ObservableProperty] private string rbSectors = string.Empty;

    // Compress tab
    [ObservableProperty] private string cpSource = string.Empty;
    [ObservableProperty] private string cpOutput = string.Empty;
    [ObservableProperty] private int cpLevel = 9;
    [ObservableProperty] private string cpVersion = "2";
    [ObservableProperty] private string cpSplit = string.Empty;

    // Decompress tab
    [ObservableProperty] private string dcCso = string.Empty;
    [ObservableProperty] private string dcOutput = string.Empty;

    // Validate tab (+ checksum group)
    [ObservableProperty] private string vaSource = string.Empty;
    [ObservableProperty] private string vaOutput = string.Empty;
    [ObservableProperty] private bool vaChecksums;
    [ObservableProperty] private string vaReport = string.Empty;
    [ObservableProperty] private string csImages = string.Empty;
    [ObservableProperty] private bool csSilent;

    // Batch tab
    [ObservableProperty] private string baDir = string.Empty;
    [ObservableProperty] private bool baRecursive;
    [ObservableProperty] private string baMode = "Extract";
    [ObservableProperty] private string baDest = string.Empty;

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

    [RelayCommand] private Task RunExtractAsync() => GuardedAsync(() => RunSingleImageAsync("extract", ExImage,
        CliCommands.Extract(RequireOne(ExImage, "image"), NullIfEmpty(ExDest), OverwriteExisting)));
    [RelayCommand] private Task RunListAsync() => GuardedAsync(() => RunSingleImageAsync("list", ExImage,
        CliCommands.List(RequireOne(ExImage, "image"))));
    [RelayCommand] private Task RunTreeAsync() => GuardedAsync(() => RunSingleImageAsync("tree", ExImage,
        CliCommands.Tree(RequireOne(ExImage, "image"))));
    [RelayCommand] private Task RunInfoAsync() => GuardedAsync(() => RunSingleImageAsync("info", ExImage,
        CliCommands.Info(RequireValue(ExImage, "image"), NullIfEmpty(ExInfoPath))));
    [RelayCommand] private Task RunUnpackAsync() => GuardedAsync(() => RunSingleImageAsync("unpack", ExImage,
        CliCommands.Unpack(RequireValue(ExImage, "image"), NullIfEmpty(ExDest))));
    [RelayCommand] private Task RunCopyOutAsync() => GuardedAsync(() => RunJobAsync("copy-out",
        CliCommands.CopyOut(RequireValue(ExImage, "image"), RequireValue(ExCopyPath, "in-image path"), RequireValue(ExCopyDest, "destination"))));

    [RelayCommand] private Task RunCreateAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(CrSource, "source directory");
        var excludes = SplitLines(CrExcludes);
        return RunJobAsync("create", CliCommands.Create(source, NullIfEmpty(CrName), excludes,
            CrSkipSystemUpdate, CrDisableXbePatch, OverwriteExisting));
    });

    [RelayCommand] private Task RunRewriteAsync() => GuardedAsync(() =>
    {
        var images = RequireLines(RwImages, "image");
        return RunJobAsync("rewrite", CliCommands.Rewrite(images, NullIfEmpty(RwOutput), NullIfEmpty(RwWorkDir),
            RwDeleteOld, RwDisableXbePatch, RwValidate, RwChecksums, RwStrict, NullIfEmpty(RwReport),
            OverwriteExisting));
    });

    [RelayCommand] private Task RunWipeAsync() => GuardedAsync(() => RunJobAsync("wipe",
        CliCommands.Wipe(RequireValue(WpImage, "image"), NullIfEmpty(WpOutput), OverwriteExisting)));
    [RelayCommand] private Task RunTrimAsync() => GuardedAsync(() => RunJobAsync("trim",
        CliCommands.Trim(RequireValue(WpImage, "image"), NullIfEmpty(WpOutput), OverwriteExisting)));

    [RelayCommand] private Task RunRebuildAsync() => GuardedAsync(() =>
    {
        var parts = RequireLines(RbParts, "component");
        var output = RequireValue(RbOutput, "output Redump ISO");
        return RunJobAsync("rebuild", CliCommands.Rebuild(parts, output, NullIfEmpty(RbSectors), OverwriteExisting));
    });

    [RelayCommand] private Task RunCompressAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(CpSource, "source directory or image");
        return RunJobAsync("compress", CliCommands.Compress(source, NullIfEmpty(CpOutput),
            Math.Clamp(CpLevel, 0, 9), string.Equals(CpVersion, "1", StringComparison.Ordinal) ? 1 : 2, NullIfEmpty(CpSplit), OverwriteExisting));
    });

    [RelayCommand] private Task RunDecompressAsync() => GuardedAsync(() => RunJobAsync("decompress",
        CliCommands.Decompress(RequireValue(DcCso, "CSO file"), NullIfEmpty(DcOutput), OverwriteExisting)));

    [RelayCommand] private Task RunValidateAsync() => GuardedAsync(() =>
    {
        var source = RequireValue(VaSource, "source ISO");
        var output = RequireValue(VaOutput, "output ISO");
        return RunJobAsync("validate", CliCommands.Validate(source, output, VaChecksums, NullIfEmpty(VaReport)));
    });

    [RelayCommand] private Task RunChecksumAsync() => GuardedAsync(() =>
    {
        var images = RequireLines(string.IsNullOrWhiteSpace(CsImages) ? VaSource : CsImages, "image");
        return RunJobAsync("checksum", CliCommands.Checksum(images, CsSilent));
    });

    [RelayCommand] private Task RunBatchAsync() => GuardedAsync(() =>
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
        return RunJobAsync("batch", CliCommands.Batch(dir, BaRecursive, modeFlag, NullIfEmpty(BaDest), OverwriteExisting));
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
