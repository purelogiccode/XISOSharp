using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.Services;
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
        Patterns = ["*.iso", "*.xiso", "*.cso", "*.1.cso", "*.cci", "*.1.cci", "*.img", "*.zar"],
    };

    // Rebuild accepts a .zar sidecar in place of the game partition (Todo #46);
    // every other picker must not offer it because XisoReader cannot open one.
    private static readonly FilePickerFileType RebuildFilter = new("Xbox images or ZAR sidecar")
    {
        Patterns = ["*.iso", "*.xiso", "*.cso", "*.1.cso", "*.cci", "*.1.cci", "*.img", "*.zar"],
    };

    private static readonly FilePickerFileType CompressedFilter = new("Compressed images")
    {
        Patterns = ["*.cso", "*.1.cso", "*.cci", "*.1.cci"],
    };

    private static readonly FilePickerFileType IsoFilter = new("ISO images")
    {
        Patterns = ["*.iso", "*.xiso"],
    };

    // The ZAR tab packs a plain image (or Redump ISO) and converts a .zar back
    // to ISO; compressed CISO/CCI inputs are not accepted by either direction.
    private static readonly FilePickerFileType ZarSourceFilter = new("Xbox images or ZAR")
    {
        Patterns = ["*.iso", "*.xiso", "*.img", "*.zar"],
    };

    // Single source for image detection (BUG-GUI-012): includes every extension
    // the pickers accept so drag-and-drop routing agrees with the filters.
    private static readonly string[] ImageExtensions = [".iso", ".xiso", ".cso", ".cci", ".img", ".zar"];

    private static readonly string[] CompressedExtensions = [".cso", ".cci"];

    private static readonly string[] ZarExtensions = [".zar"];

    // TabControl order in MainWindow.axaml.
    private const int ExtractTab = 0;
    private const int CreateTab = 1;
    private const int RewriteTab = 2;
    private const int RebuildTab = 3;
    private const int DecompressTab = 6;
    private const int XboxTab = 7;
    private const int BatchTab = 9;

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
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            Log.Information("MainWindow initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainWindow initialization failed");
            throw;
        }
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    /// <summary>
    /// Applies the immersive dark title bar on Windows so the native frame matches
    /// the pinned dark theme. Best effort: unsupported builds keep the OS default.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TryApplyDarkTitleBar();
    }

    private void TryApplyDarkTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+, 19 on 1809-1909.
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dark title bar is unavailable on this system");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Null-safe VM access. DataContext is thread-affine, so this property is
    // only safe from the UI thread; asynchronous picker helpers resume on the
    // thread pool after ConfigureAwait(false) and must use LogGuiMessage.
    private MainViewModel? VmOrNull => DataContext as MainViewModel;

    /// <summary>
    /// Logs a GUI message from any thread. DataContext access is marshaled to
    /// the UI thread so exception handlers running on a pool thread cannot
    /// turn a handled picker failure into a second, unhandled exception.
    /// </summary>
    private void LogGuiMessage(string line)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                VmOrNull?.LogMessage(line);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    VmOrNull?.LogMessage(line);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "GUI message logging failed on the UI thread");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GUI message logging failed");
        }
    }

    private void Donate_Click(object? sender, RoutedEventArgs e)
    {
        if (!UrlOpener.TryOpen("https://www.purelogiccode.com/donate"))
        {
            LogGuiMessage("[GUI] Could not open the donation page in the default browser.");
        }
    }

    private async void About_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            AboutWindow about = new();
            await about.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "About dialog failed");
        }
    }

    /// <summary>
    /// Tunnel handler for the F8 screenshot shortcut; runs before focused
    /// controls can consume the key.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8)
        {
            return;
        }

        e.Handled = true;
        TakeScreenshot();
    }

    private void TakeScreenshot()
    {
        try
        {
            ScreenshotResult? result = ScreenshotService.CaptureActive(this, DateTime.Now);
            if (result is null)
            {
                LogGuiMessage("[GUI] Screenshot failed; see the log file for details.");
                return;
            }

            string note = result.UsedFallbackFolder
                ? " (application folder not writable; saved to the fallback location)"
                : string.Empty;
            LogGuiMessage($"[GUI] Screenshot saved{note}: {result.FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshot shortcut failed");
            LogGuiMessage($"[GUI] Screenshot failed: {ex.Message}");
        }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (VmOrNull is { IsRunning: true } vm)
            {
                vm.CancelRunCommand.Execute(null);
            }

            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exit failed");
        }
    }

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
        }
    }

    private async Task<string?> PickSingleFileAsync(IReadOnlyList<FilePickerFileType> filters, string title)
    {
        try
        {
            List<string> files = await PickFilesAsync(filters, title, allowMultiple: false).ConfigureAwait(false);
            return files.Count > 0 ? files[0] : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "File picker failed: {Title}", title);
            LogGuiMessage($"[GUI] File picker failed: {ex.Message}");
            return null;
        }
    }

    private async Task<List<string>> PickFilesAsync(IReadOnlyList<FilePickerFileType> filters, string title,
        bool allowMultiple)
    {
        try
        {
            IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("File picker unavailable (no storage provider): {Title}", title);
                return [];
            }

            IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple,
                FileTypeFilter = [.. filters, FilePickerFileTypes.All],
            }).ConfigureAwait(false);

            List<string> paths = [];
            foreach (IStorageFile file in picked)
            {
                string? localPath = StorageItemPath.TryGetLocalPath(file);
                if (localPath is null)
                {
                    Log.ForContext(BugReportSink.NoBugReportProperty, true)
                        .Warning("File picker returned a non-local path: {Title}", title);
                    continue;
                }

                paths.Add(localPath);
            }

            return paths;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "File picker failed: {Title}", title);
            LogGuiMessage($"[GUI] File picker failed: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("Folder picker unavailable (no storage provider): {Title}", title);
                return null;
            }

            IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            }).ConfigureAwait(false);

            if (picked.Count == 0)
            {
                return null;
            }

            string? localPath = StorageItemPath.TryGetLocalPath(picked[0]);
            if (localPath is null)
            {
                Log.ForContext(BugReportSink.NoBugReportProperty, true)
                    .Warning("Folder picker returned a non-local path: {Title}", title);
                LogGuiMessage(FolderPickerDiagnostics.NonLocalSelectionMessage);
            }

            return localPath;
        }
        catch (ArgumentException ex) when (FolderPickerDiagnostics.IsMissingSelection(ex))
        {
            // Avalonia's BCL storage provider throws when the selected folder
            // no longer exists (it can be deleted from inside the dialog):
            // https://github.com/AvaloniaUI/Avalonia/issues/17908. That is a
            // cancelled selection with a known cause, not an application bug.
            Log.ForContext(BugReportSink.NoBugReportProperty, true)
                .Warning("Folder picker selected a path that is not an existing directory: {Title}", title);
            LogGuiMessage(FolderPickerDiagnostics.MissingSelectionMessage);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Folder picker failed: {Title}", title);
            LogGuiMessage($"[GUI] Folder picker failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PickSaveAsync(string title, string? suggestedName)
    {
        try
        {
            IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                Log.Warning("Save picker unavailable (no storage provider): {Title}", title);
                return null;
            }

            IStorageFile? picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
            }).ConfigureAwait(false);

            string? localPath = StorageItemPath.TryGetLocalPath(picked);
            if (picked is not null && localPath is null)
            {
                Log.ForContext(BugReportSink.NoBugReportProperty, true)
                    .Warning("Save picker returned a non-local path: {Title}", title);
                LogGuiMessage(FolderPickerDiagnostics.NonLocalSelectionMessage);
            }

            return localPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save picker failed: {Title}", title);
            LogGuiMessage($"[GUI] Save picker failed: {ex.Message}");
            return null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            e.DragEffects = !Vm.IsRunning && e.DataTransfer.TryGetFiles()?.Length > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drag-over handling failed");
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

            List<string> paths = e.DataTransfer.TryGetFiles()
                ?.Select(f =>
                {
                    string? localPath = StorageItemPath.TryGetLocalPath(f);
                    if (localPath is null)
                    {
                        Log.ForContext(BugReportSink.NoBugReportProperty, true)
                            .Warning("Skipping dropped item with unreadable path");
                    }

                    return localPath;
                })
                .Where(p => p is not null)
                .Cast<string>()
                .ToList() ?? [];

            if (paths.Count == 0)
            {
                return;
            }

            // GOD/SVOD containers and STFS packages have no image extension;
            // route them to the Xbox 360 tab before the generic image/folder
            // handlers can mistake them for pack sources.
            if (paths.Count == 1 && IsXboxContainer(paths[0]))
            {
                RouteXbox(paths[0]);
                return;
            }

            List<string> images = paths.Where(p => File.Exists(p) && IsImage(p)).ToList();
            List<string> dirs = paths.Where(Directory.Exists).ToList();
            foreach (string skipped in paths.Except(images, StringComparer.OrdinalIgnoreCase)
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
                    // .zar sidecars cannot be rewritten (CLI -r opens ISO/CISO
                    // only); route them to Rebuild like the single-drop path
                    // does (Todo #46).
                    List<string> zarImages = images.Where(IsZar).ToList();
                    List<string> rewriteImages = images.Where(p => !IsZar(p)).ToList();

                    if (rewriteImages.Count > 0)
                    {
                        Vm.RwImages = AppendDistinctLines(Vm.RwImages, rewriteImages);
                        Vm.LogMessage($"[GUI] Drop: {rewriteImages.Count} image(s) added to the Rewrite tab.");
                    }

                    if (zarImages.Count > 0)
                    {
                        Vm.RbParts = AppendLines(Vm.RbParts, zarImages);
                        Vm.LogMessage($"[GUI] Drop: {zarImages.Count} ZAR sidecar(s) added to the Rebuild tab.");
                    }

                    OpTabs.SelectedIndex = rewriteImages.Count > 0 ? RewriteTab : RebuildTab;
                }

                foreach (string dir in dirs)
                {
                    DropDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drop handling failed");
            try
            {
                Vm.LogMessage($"[GUI] Drop failed: {ex.Message}");
            }
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
            if (IsXboxContainer(image))
            {
                RouteXbox(image);
            }
            else if (IsZar(image))
            {
                Vm.ExImage = image;
                OpTabs.SelectedIndex = ExtractTab;
                Vm.LogMessage($"[GUI] Drop: ZAR routed to the Extract tab (list/extract/checksum): {image}");
            }
            else if (IsCompressed(image))
            {
                Vm.DcCso = image;
                OpTabs.SelectedIndex = DecompressTab;
                Vm.LogMessage($"[GUI] Drop: compressed image routed to the Decompress tab: {image}");
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
            Vm.LogMessage($"[GUI] Drop failed: {ex.Message}");
        }
    }

    private void DropDirectory(string dir)
    {
        try
        {
            if (IsXboxContainer(dir))
            {
                RouteXbox(dir);
                return;
            }

            // A folder full of .iso images reads as a batch library; anything
            // else (including .cso/.zar/.img-only folders, which CLI --batch
            // would reject with "no .iso files found") reads as files to pack.
            bool hasImages;
            try
            {
                hasImages = Directory.EnumerateFiles(dir).Any(static p =>
                    Path.GetExtension(p).Equals(".iso", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Environmental (permissions/IO), not a bug: log locally without
                // filing a bug report, and tell the user in the log panel.
                Log.ForContext(BugReportSink.NoBugReportProperty, true)
                    .Warning(ex, "Drop skipped (cannot read folder) {Dir}", dir);
                try
                {
                    Vm.LogMessage($"[GUI] Drop skipped (cannot read folder): {dir} ({ex.Message})");
                }
                catch
                {
                    // ignored
                }

                return;
            }

            if (hasImages)
            {
                Vm.BaDir = dir;
                OpTabs.SelectedIndex = BatchTab;
                Vm.LogMessage($"[GUI] Drop: folder with images routed to the Batch tab: {dir}");
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
            try
            {
                Vm.LogMessage($"[GUI] Drop failed: {ex.Message}");
            }
            catch
            {
                // ignored
            }
        }
    }

    private void RouteXbox(string path)
    {
        Vm.XbSource = path;
        OpTabs.SelectedIndex = XboxTab;
        Vm.LogMessage($"[GUI] Drop: GOD/STFS input routed to the Xbox 360 tab: {path}");
    }

    /// <summary>
    /// Detects a GOD/SVOD container (header file or <c>.data</c> folder) or a
    /// single-file STFS package. A parent folder containing a GOD header is also
    /// accepted so dropping the game folder works.
    /// </summary>
    private static bool IsXboxContainer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return StfsSharp.GodHeader.IsGodContainer(path) || StfsSharp.StfsPackage.IsStfsPackage(path);
            }

            if (!Directory.Exists(path))
            {
                return false;
            }

            if (StfsSharp.GodHeader.IsGodContainer(path))
            {
                return true;
            }

            return Directory.EnumerateFiles(path).Any(static file =>
                StfsSharp.GodHeader.IsGodContainer(file) || StfsSharp.StfsPackage.IsStfsPackage(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsCompressed(string path) =>
        CompressedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsZar(string path) =>
        ZarExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string AppendDistinctLines(string current, IEnumerable<string> added)
    {
        HashSet<string> existing = new(
            current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()),
            StringComparer.OrdinalIgnoreCase);
        List<string> fresh = added.Select(a => a.Trim()).Where(a => existing.Add(a)).ToList();
        return AppendLines(current, fresh);
    }

    private static string AppendLines(string current, IEnumerable<string> added)
    {
        List<string> lines = current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.AddRange(added);
        return string.Join(Environment.NewLine, lines);
    }

    private async void BrowseExImage_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([ImageFilter], "Select image").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.ExImage = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse extract image failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseExDest_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.ExDest = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse extract destination failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseCrSource_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CrSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse create source failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void AddRwImages_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            List<string> picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true)
                .ConfigureAwait(false);
            if (picked.Count != 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RwImages = AppendLines(vm.RwImages, picked));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add rewrite images failed");
            LogGuiMessage($"[GUI] Add images failed: {ex.Message}");
        }
    }

    private async void BrowseRwOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSaveAsync("Rewrite output", "rewritten.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RwOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseRwWorkDir_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select work directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RwWorkDir = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite work directory failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseRwReport_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RwReport = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rewrite report failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseWpImage_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([IsoFilter], "Select image").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.WpImage = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse wipe image failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void AddRbParts_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            List<string> picked = await PickFilesAsync([RebuildFilter], "Add rebuild components", allowMultiple: true)
                .ConfigureAwait(false);
            if (picked.Count != 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RbParts = AppendLines(vm.RbParts, picked));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add rebuild parts failed");
            LogGuiMessage($"[GUI] Add rebuild parts failed: {ex.Message}");
        }
    }

    private async void BrowseRbOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSaveAsync("Redump output", "redump.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RbOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rebuild output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseRbSectors_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([FilePickerFileTypes.TextPlain], "Select sectors file")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.RbSectors = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse rebuild sectors failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseCpSourceFile_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([IsoFilter], "Select source image").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CpSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress source file failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseCpSourceFolder_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CpSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress source folder failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseCpOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            bool cci = string.Equals(vm.CpFormat, "CCI", StringComparison.OrdinalIgnoreCase);
            string? picked = await PickSaveAsync(cci ? "CCI output" : "CISO output",
                cci ? "game.cci" : "game.cso").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CpOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse compress output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseZaSourceFile_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            // The ZAR tab is bidirectional: a .zar source converts back to ISO.
            string? picked = await PickSingleFileAsync([ZarSourceFilter], "Select source image or ZAR")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.ZaSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse ZAR source failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseZaOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = vm.ZaIsConvert
                ? await PickSaveAsync("ISO output", "game.iso").ConfigureAwait(false)
                : await PickSaveAsync("ZAR output", "game.zar").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.ZaOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse ZAR output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseDcCso_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([CompressedFilter], "Select CISO/CCI").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.DcCso = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse decompress CISO/CCI failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseDcOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSaveAsync("ISO output", "game.iso").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.DcOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse decompress output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseVaSource_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([IsoFilter], "Select source ISO").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.VaSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate source failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseVaOutput_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([IsoFilter], "Select output ISO").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.VaOutput = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate output failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseVaReport_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.VaReport = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse validate report failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void AddCsImages_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            List<string> picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true)
                .ConfigureAwait(false);
            if (picked.Count != 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CsImages = AppendLines(vm.CsImages, picked));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add checksum images failed");
            LogGuiMessage($"[GUI] Add images failed: {ex.Message}");
        }
    }

    private async void BrowseBaDir_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select batch directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.BaDir = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse batch directory failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseBaDest_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.BaDest = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse batch destination failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseXbSourceFile_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([ImageFilter], "Select Xbox 360 image or package")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.XbSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse Xbox 360 source file failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseXbSourceFolder_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select GOD/SVOD header or .data folder")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.XbSource = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse Xbox 360 source folder failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseXbDest_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickFolderAsync("Select Xbox 360 destination")
                .ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.XbDest = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse Xbox 360 destination failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }

    private async void BrowseCliPath_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            MainViewModel vm = Vm;
            string? picked = await PickSingleFileAsync([], "Select XISOSharp executable").ConfigureAwait(false);
            if (picked is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => vm.CliPath = picked);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse CLI path failed");
            LogGuiMessage($"[GUI] Browse failed: {ex.Message}");
        }
    }
}
