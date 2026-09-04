using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.ViewModels;

namespace XISOSharp.Gui.Views;

/// <summary>
/// Main GUI window. Hosts the tabbed operations UI, file/folder pickers, and drag-and-drop
/// routing that assigns dropped images and folders to the matching view-model fields and tabs.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ImageFilter = new("Xbox images")
    {
        Patterns = ["*.iso", "*.xiso", "*.cso", "*.zar", "*.img"],
    };

    private static readonly FilePickerFileType CsoFilter = new("CISO images")
    {
        Patterns = ["*.cso", "*.1.cso"],
    };

    private static readonly FilePickerFileType IsoFilter = new("ISO images")
    {
        Patterns = ["*.iso", "*.xiso"],
    };

    private static readonly string[] ImageExtensions = [".iso", ".xiso", ".img", ".zar"];

    private static readonly string[] CsoExtensions = [".cso"];

    // TabControl order in MainWindow.axaml.
    private const int ExtractTab = 0;
    private const int CreateTab = 1;
    private const int RewriteTab = 2;
    private const int DecompressTab = 5;
    private const int BatchTab = 7;

    /// <summary>
    /// Initializes the window and subscribes drag-over/drop handlers.
    /// </summary>
    public MainWindow()
    {
        try
        {
            InitializeComponent();
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            Log.Information("MainWindow initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainWindow initialization failed");
            BugReporter.ReportException(ex, "MainWindow initialization failed");
            throw;
        }
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private void LogBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        try
        {
            if (sender is TextBox box)
            {
                box.CaretIndex = int.MaxValue;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Log auto-scroll failed");
            BugReporter.ReportException(ex, "Log auto-scroll failed");
        }
    }

    private async Task<string?> PickSingleFileAsync(IReadOnlyList<FilePickerFileType> filters, string title)
    {
        try
        {
            var files = await PickFilesAsync(filters, title, allowMultiple: false).ConfigureAwait(false);
            return files.Count > 0 ? files[0] : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "File picker failed: {Title}", title);
            BugReporter.ReportException(ex, $"File picker failed: {title}");
            Vm.LogMessage($"[GUI] File picker failed: {ex.Message}");
            return null;
        }
    }

    private async Task<List<string>> PickFilesAsync(IReadOnlyList<FilePickerFileType> filters, string title,
        bool allowMultiple)
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("File picker unavailable (no storage provider): {Title}", title);
                return [];
            }

            var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple,
                FileTypeFilter = [.. filters, FilePickerFileTypes.All],
            }).ConfigureAwait(false);
            return picked.Select(f => f.Path.LocalPath).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "File picker failed: {Title}", title);
            BugReporter.ReportException(ex, $"File picker failed: {title}");
            Vm.LogMessage($"[GUI] File picker failed: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("Folder picker unavailable (no storage provider): {Title}", title);
                return null;
            }

            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            }).ConfigureAwait(false);
            return picked.Count > 0 ? picked[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Folder picker failed: {Title}", title);
            BugReporter.ReportException(ex, $"Folder picker failed: {title}");
            Vm.LogMessage($"[GUI] Folder picker failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PickSaveAsync(string title, string? suggestedName)
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("Save picker unavailable (no storage provider): {Title}", title);
                return null;
            }

            var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
            }).ConfigureAwait(false);
            return picked?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save picker failed: {Title}", title);
            BugReporter.ReportException(ex, $"Save picker failed: {title}");
            Vm.LogMessage($"[GUI] Save picker failed: {ex.Message}");
            return null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = !Vm.IsRunning && e.DataTransfer?.TryGetFiles()?.Length > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drag-over handling failed");
            BugReporter.ReportException(ex, "Drag-over handling failed");
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (Vm.IsRunning)
            {
                Vm.LogMessage("[GUI] Drop ignored — a run is already in progress.");
                return;
            }

            var paths = e.DataTransfer.TryGetFiles()
                ?.Select(f =>
                {
                    try
                    {
                        return f.Path.LocalPath;
                    }
                    catch (InvalidOperationException ex)
                    {
                        Log.Warning(ex, "Skipping dropped item with unreadable path");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Reading dropped item path failed");
                        BugReporter.ReportException(ex, "Reading dropped item path failed");
                        return null;
                    }
                })
                .Where(p => p is not null)
                .Cast<string>()
                .ToList() ?? [];

            if (paths.Count == 0)
            {
                return;
            }

            var images = paths.Where(p => File.Exists(p) && IsImage(p)).ToList();
            var dirs = paths.Where(Directory.Exists).ToList();
            foreach (var skipped in paths.Except(images, StringComparer.OrdinalIgnoreCase)
                         .Except(dirs, StringComparer.OrdinalIgnoreCase))
            {
                Vm.LogMessage($"[GUI] Drop skipped (not an image or folder): {skipped}");
            }

            if (images.Count == 1 && dirs.Count == 0)
            {
                DropSingleImage(images[0]);
            }
            else
            {
                if (images.Count > 0)
                {
                    Vm.RwImages = AppendDistinctLines(Vm.RwImages, images);
                    OpTabs.SelectedIndex = RewriteTab;
                    Vm.LogMessage($"[GUI] Drop: {images.Count} image(s) added to the Rewrite tab.");
                }

                foreach (var dir in dirs)
                {
                    DropDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drop handling failed");
            BugReporter.ReportException(ex, "Drop handling failed");
            try { Vm.LogMessage($"[GUI] Drop failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private void DropSingleImage(string image)
    {
        try
        {
            if (IsCso(image))
            {
                Vm.DcCso = image;
                OpTabs.SelectedIndex = DecompressTab;
                Vm.LogMessage($"[GUI] Drop: CSO routed to the Decompress tab: {image}");
            }
            else
            {
                Vm.ExImage = image;
                OpTabs.SelectedIndex = ExtractTab;
                Vm.LogMessage($"[GUI] Drop: image routed to the Extract tab: {image}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DropSingleImage failed for {Image}", image);
            BugReporter.ReportException(ex, $"DropSingleImage failed for {image}");
            Vm.LogMessage($"[GUI] Drop failed: {ex.Message}");
        }
    }

    private void DropDirectory(string dir)
    {
        try
        {
            // A folder full of ISOs reads as a batch library; anything else reads
            // as files to pack into a new image.
            bool hasIsos;
            try
            {
                hasIsos = Directory.EnumerateFiles(dir, "*.iso").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { Vm.LogMessage($"[GUI] Drop skipped (cannot read folder): {dir} ({ex.Message})"); }
                catch
                {
                    // ignored
                }
                return;
            }

            if (hasIsos)
            {
                Vm.BaDir = dir;
                OpTabs.SelectedIndex = BatchTab;
                Vm.LogMessage($"[GUI] Drop: folder with ISOs routed to the Batch tab: {dir}");
            }
            else
            {
                Vm.CrSource = dir;
                OpTabs.SelectedIndex = CreateTab;
                Vm.LogMessage($"[GUI] Drop: folder routed to the Create tab: {dir}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DropDirectory failed for {Dir}", dir);
            BugReporter.ReportException(ex, $"DropDirectory failed for {dir}");
            try { Vm.LogMessage($"[GUI] Drop failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private static bool IsImage(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
               || IsCso(path);
    }

    private static bool IsCso(string path)
    {
        return CsoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string AppendDistinctLines(string current, IEnumerable<string> added)
    {
        var existing = new HashSet<string>(
            current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var fresh = added.Select(a => a.Trim()).Where(a => existing.Add(a)).ToList();
        return AppendLines(current, fresh);
    }

    private static string AppendLines(string current, IEnumerable<string> added)
    {
        var lines = current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.AddRange(added);
        return string.Join(Environment.NewLine, lines);
    }

    private async void BrowseExImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([ImageFilter], "Select image").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.ExImage = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse extract image failed");
            BugReporter.ReportException(ex, "Browse extract image failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseExDest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.ExDest = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse extract destination failed");
            BugReporter.ReportException(ex, "Browse extract destination failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseCrSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.CrSource = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse create source failed");
            BugReporter.ReportException(ex, "Browse create source failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void AddRwImages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true).ConfigureAwait(false);
            Vm.RwImages = AppendLines(Vm.RwImages, picked);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add rewrite images failed");
            BugReporter.ReportException(ex, "Add rewrite images failed");
            try { Vm.LogMessage($"[GUI] Add images failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseRwOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("Rewrite output", "rewritten.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.RwOutput = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite output failed");
            BugReporter.ReportException(ex, "Browse rewrite output failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseRwWorkDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select work directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.RwWorkDir = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite work directory failed");
            BugReporter.ReportException(ex, "Browse rewrite work directory failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseRwReport_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.RwReport = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite report failed");
            BugReporter.ReportException(ex, "Browse rewrite report failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseWpImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([IsoFilter], "Select image").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.WpImage = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse wipe image failed");
            BugReporter.ReportException(ex, "Browse wipe image failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void AddRbParts_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFilesAsync([ImageFilter], "Add rebuild components", allowMultiple: true)
                .ConfigureAwait(false);
            Vm.RbParts = AppendLines(Vm.RbParts, picked);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add rebuild parts failed");
            BugReporter.ReportException(ex, "Add rebuild parts failed");
            try { Vm.LogMessage($"[GUI] Add rebuild parts failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseRbOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("Redump output", "redump.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.RbOutput = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rebuild output failed");
            BugReporter.ReportException(ex, "Browse rebuild output failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseRbSectors_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([FilePickerFileTypes.TextPlain], "Select sectors file")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.RbSectors = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rebuild sectors failed");
            BugReporter.ReportException(ex, "Browse rebuild sectors failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseCpSourceFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([IsoFilter], "Select source image").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.CpSource = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress source file failed");
            BugReporter.ReportException(ex, "Browse compress source file failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseCpSourceFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.CpSource = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress source folder failed");
            BugReporter.ReportException(ex, "Browse compress source folder failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseCpOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("CSO output", "game.cso").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.CpOutput = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress output failed");
            BugReporter.ReportException(ex, "Browse compress output failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseDcCso_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([CsoFilter], "Select CSO").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.DcCso = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse decompress CSO failed");
            BugReporter.ReportException(ex, "Browse decompress CSO failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseDcOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("ISO output", "game.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.DcOutput = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse decompress output failed");
            BugReporter.ReportException(ex, "Browse decompress output failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseVaSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([IsoFilter], "Select source ISO").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.VaSource = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate source failed");
            BugReporter.ReportException(ex, "Browse validate source failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseVaOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([IsoFilter], "Select output ISO").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.VaOutput = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate output failed");
            BugReporter.ReportException(ex, "Browse validate output failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseVaReport_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.VaReport = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate report failed");
            BugReporter.ReportException(ex, "Browse validate report failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void AddCsImages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true).ConfigureAwait(false);
            Vm.CsImages = AppendLines(Vm.CsImages, picked);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add checksum images failed");
            BugReporter.ReportException(ex, "Add checksum images failed");
            try { Vm.LogMessage($"[GUI] Add images failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseBaDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select batch directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.BaDir = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse batch directory failed");
            BugReporter.ReportException(ex, "Browse batch directory failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseBaDest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.BaDest = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse batch destination failed");
            BugReporter.ReportException(ex, "Browse batch destination failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }

    private async void BrowseCliPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var picked = await PickSingleFileAsync([], "Select XISOSharp executable").ConfigureAwait(false);
            if (picked is not null)
            {
                Vm.CliPath = picked;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse CLI path failed");
            BugReporter.ReportException(ex, "Browse CLI path failed");
            try { Vm.LogMessage($"[GUI] Browse failed: {ex.Message}"); }
            catch
            {
                // ignored
            }
        }
    }
}