using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using XISOSharp;
using XISOSharpTester.Logging;
using XisoNode = XISOSharp.ExplorerNode;

namespace XISOSharpTester.ViewModels;

#pragma warning disable MA0048 // File name must match type name — explorer tab is grouped intentionally

/// <summary>
/// Explore tab of the Tester main page (TODO #11, xdvdfs #120): in-process
/// image explorer bound to <see cref="XisoExplorer"/>. A <c>TreeView</c> lists
/// the image via <c>ListDirectory</c>/<c>GetEntryInfo</c>/<c>GetVolumeInfo</c>;
/// the selected node offers per-node <c>CopyOut</c>, <c>ComputeFileHash</c>
/// display, and an <c>XexInfo</c> panel.
/// </summary>
internal partial class MainViewModel
{
    private XisoExplorer? _explorer;

    /// <summary>Gets the open image's root rows bound to the explore tree.</summary>
    public ObservableCollection<ExplorerTreeNode> ExplorerRoots { get; } = [];

    private string _exploreImagePath = string.Empty;

    /// <summary>Gets or sets the explored image path.</summary>
    public string ExploreImagePath
    {
        get => _exploreImagePath;
        set
        {
            _exploreImagePath = value;
            OnPropertyChanged();
        }
    }

    private string _exploreVolumeText = "No image open.";

    /// <summary>Gets or sets the volume summary line for the open image.</summary>
    public string ExploreVolumeText
    {
        get => _exploreVolumeText;
        set
        {
            _exploreVolumeText = value;
            OnPropertyChanged();
        }
    }

    private ExplorerTreeNode? _selectedExplorerNode;

    /// <summary>Gets the currently selected explore-tree node.</summary>
    public ExplorerTreeNode? SelectedExplorerNode
    {
        get => _selectedExplorerNode;
        private set
        {
            _selectedExplorerNode = value;
            OnPropertyChanged();
        }
    }

    private string _explorerDetailsText = string.Empty;

    /// <summary>Gets or sets the selected node's detail lines.</summary>
    public string ExplorerDetailsText
    {
        get => _explorerDetailsText;
        set
        {
            _explorerDetailsText = value;
            OnPropertyChanged();
        }
    }

    private string _explorerHashText = string.Empty;

    /// <summary>Gets or sets the last computed hash line.</summary>
    public string ExplorerHashText
    {
        get => _explorerHashText;
        set
        {
            _explorerHashText = value;
            OnPropertyChanged();
        }
    }

    private string _explorerXexText = string.Empty;

    /// <summary>Gets or sets the XEX panel text for the selected node.</summary>
    public string ExplorerXexText
    {
        get => _explorerXexText;
        set
        {
            _explorerXexText = value;
            OnPropertyChanged();
        }
    }

    private bool _showExplorerXex;

    /// <summary>Gets or sets whether the XEX panel is shown.</summary>
    public bool ShowExplorerXex
    {
        get => _showExplorerXex;
        set
        {
            _showExplorerXex = value;
            OnPropertyChanged();
        }
    }

    private bool _isExplorerBusy;

    /// <summary>Gets or sets whether an explore job is running.</summary>
    public bool IsExplorerBusy
    {
        get => _isExplorerBusy;
        set
        {
            _isExplorerBusy = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _explorerStatusText = string.Empty;

    /// <summary>Gets or sets the explore status line.</summary>
    public string ExplorerStatusText
    {
        get => _explorerStatusText;
        set
        {
            _explorerStatusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the browse-for-explore-image command.</summary>
    public ICommand BrowseExploreImageCommand { get; private set; } = null!;

    /// <summary>Gets the open-explore-image command.</summary>
    public ICommand OpenExploreImageCommand { get; private set; } = null!;

    /// <summary>Gets the refresh-explore-image command.</summary>
    public ICommand RefreshExploreCommand { get; private set; } = null!;

    /// <summary>Gets the close-explore-image command.</summary>
    public ICommand CloseExploreImageCommand { get; private set; } = null!;

    /// <summary>Gets the copy-out-selected-node command.</summary>
    public ICommand CopyOutNodeCommand { get; private set; } = null!;

    /// <summary>Gets the hash-selected-node command.</summary>
    public ICommand HashNodeCommand { get; private set; } = null!;

    /// <summary>
    /// Wires the explore commands. Called from the main constructor.
    /// Async handlers use <see cref="AsyncRelayCommand"/> so faults are observed
    /// to the session log instead of escaping as unobserved Tasks (TST-003).
    /// </summary>
    private void InitExploreCommands()
    {
        BrowseExploreImageCommand = new RelayCommand(_ => BrowseExploreImage());
        OpenExploreImageCommand = new AsyncRelayCommand(_ => OpenExploreImageAsync(), null,
            ex => AddLog($"Explore open failed: {ex.Message}"));
        RefreshExploreCommand = new AsyncRelayCommand(
            _ => OpenExploreImageAsync(refresh: true),
            _ => _explorer is not null && !IsExplorerBusy,
            ex => AddLog($"Explore refresh failed: {ex.Message}"));
        CloseExploreImageCommand = new RelayCommand(
            _ => CloseExploreImage(),
            _ => _explorer is not null && !IsExplorerBusy);
        CopyOutNodeCommand = new AsyncRelayCommand(_ => CopyOutNodeAsync(), null,
            ex => AddLog($"Explore copy-out failed: {ex.Message}"));
        HashNodeCommand =
            new AsyncRelayCommand(_ => HashNodeAsync(), null, ex => AddLog($"Explore hash failed: {ex.Message}"));
    }

    /// <summary>
    /// Raises explore <c>CanExecuteChanged</c> promptly (TST-008). Called from
    /// <c>InvalidateCommands</c> (already on the UI thread).
    /// </summary>
    private void InvalidateExploreCommands()
    {
        (BrowseExploreImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenExploreImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshExploreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CloseExploreImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyOutNodeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (HashNodeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void BrowseExploreImage()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select XISO image to explore",
                Filter = "XISO images (*.iso;*.cso)|*.iso;*.cso|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                ExploreImagePath = dlg.FileName;
                AddLog($"Explore image set to: {ExploreImagePath}");
                Log.Information("Explore image set to {Path}", ExploreImagePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse explore image failed");
            BugReporter.ReportException(ex, "Browse explore image failed");
            AddLog($"Error selecting explore image: {ex.Message}");
        }
    }

    private async Task OpenExploreImageAsync(bool refresh = false)
    {
        if (IsExplorerBusy)
            return;

        var path = refresh && _explorer is not null
            ? _explorer.IsoPath
            : ExploreImagePath.Trim();
        if (string.IsNullOrEmpty(path))
        {
            AddLog("Select an image to explore first.");
            return;
        }

        IsExplorerBusy = true;
        ExplorerStatusText = $"Opening {Path.GetFileName(path)}...";
        try
        {
            var (explorer, roots) = await Task.Run(() =>
            {
                var opened = new XisoExplorer(path);
                return (opened, opened.ListChildren("/"));
            }).ConfigureAwait(false);

            OnUi(() =>
            {
                _explorer = explorer;
                ExploreImagePath = path;
                ExplorerRoots.Clear();
                foreach (var node in roots)
                    ExplorerRoots.Add(new ExplorerTreeNode(node, LoadExploreChildren));
                SetSelectedExplorerNode(null);
                ExplorerHashText = string.Empty;
                var vol = explorer.Volume;
                ExploreVolumeText = string.Format(CultureInfo.InvariantCulture,
                    "Volume: valid XISO — {0:N0} bytes ({1:N0} sectors), root table @ sector {2} ({3:N0} bytes), disc lseek {4}.",
                    vol.FileLength, vol.TotalSectors, vol.RootDirSector, vol.RootDirSize, vol.DiscLseek);
                ExplorerStatusText = $"Open: {roots.Count} root entr{(roots.Count == 1 ? "y" : "ies")}.";
                AddLog($"Explore opened: {path} ({roots.Count} root entries).");
                Log.Information("Explore opened {Path} ({Count} root entries)", path, roots.Count);
                CommandManager.InvalidateRequerySuggested();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore open failed for {Path}", path);
            BugReporter.ReportException(ex, $"Explore open failed for {path}");
            OnUi(() =>
            {
                ExplorerStatusText = $"Open failed: {ex.Message}";
                AddLog($"Explore open failed: {ex.Message}");
            });
        }
        finally
        {
            OnUi(() => IsExplorerBusy = false);
        }
    }

    private IReadOnlyList<XisoNode> LoadExploreChildren(ExplorerTreeNode node) =>
        // Invoked off the UI thread via ExplorerTreeNode's background load (TST-010);
        // directory listings are table reads, safe here. The node marshals the
        // resulting list back to the UI thread before touching its bound collection.
        _explorer?.ListChildren(node.FullPath) ?? [];

    private void CloseExploreImage()
    {
        _explorer = null;
        ExplorerRoots.Clear();
        SetSelectedExplorerNode(null);
        ExplorerHashText = string.Empty;
        ExploreVolumeText = "No image open.";
        ExplorerStatusText = string.Empty;
        AddLog("Explore image closed.");
        Log.Information("Explore image closed");
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Selects an explore-tree node (called from the view's selection event),
    /// refreshing the details text and probing the XEX header for files.
    /// </summary>
    internal void SetSelectedExplorerNode(ExplorerTreeNode? node)
    {
        SelectedExplorerNode = node;
        ShowExplorerXex = false;
        if (node?.IsDummy != false)
        {
            ExplorerDetailsText = string.Empty;
            return;
        }

        ExplorerDetailsText = string.Format(CultureInfo.InvariantCulture,
            "Path: {0}\nType: {1}\nSize: {2} ({3:N0} bytes)\nStart sector: {4}\nAttributes: 0x{5:X2}",
            node.FullPath, node.IsDirectory ? "directory" : "file",
            ExplorerTreeNode.FormatSize(node.Size), node.Size,
            node.StartSector, node.Attributes);

        if (!node.IsDirectory)
            LoadXexForSelectionAsync(node);
    }

    private async void LoadXexForSelectionAsync(ExplorerTreeNode node)
    {
        // async void by design (TST-003): selection-changed has no Task to observe,
        // so faults are caught internally and logged; no unobserved Task escapes.
        var explorer = _explorer;
        if (explorer is null)
            return;

        try
        {
            var xex = await Task.Run(() => explorer.GetXexInfo(node.FullPath)).ConfigureAwait(false);
            OnUi(() =>
            {
                if (!ReferenceEquals(SelectedExplorerNode, node))
                    return;

                if (xex is null)
                {
                    ShowExplorerXex = false;
                    return;
                }

                ExplorerXexText = string.Format(CultureInfo.InvariantCulture,
                    "XEX2 executable\nModule flags: 0x{0:X}\nEntry point: 0x{1:X8}\nImage base: 0x{2:X8}\n" +
                    "Image size: 0x{3:X}  Load address: 0x{4:X8}\nRegion: 0x{5:X}  Media types: 0x{6:X}\n" +
                    "Media ID: 0x{7:X8}  Title ID: 0x{8:X8}  Version: {9}\nPlatform: {10}  Disc: {11}/{12}\n" +
                    "Encryption: {13}  Compression: {14}",
                    xex.ModuleFlags, xex.EntryPoint, xex.ImageBaseAddress,
                    xex.ImageSize, xex.LoadAddress, xex.Region, xex.AllowedMediaTypes,
                    xex.MediaId, xex.TitleId, xex.Version,
                    xex.Platform, xex.DiscNumber, xex.DiscCount,
                    xex.EncryptionType, xex.CompressionType);
                ShowExplorerXex = true;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore XEX probe failed for {Path}", node.FullPath);
            BugReporter.ReportException(ex, $"Explore XEX probe failed for {node.FullPath}");
        }
    }

    private async Task CopyOutNodeAsync()
    {
        var node = SelectedExplorerNode;
        var explorer = _explorer;
        if (node?.IsDummy != false || explorer is null)
        {
            AddLog("Select a file or directory in the explore tree first.");
            return;
        }

        if (IsExplorerBusy)
            return;

        string destPath;
        try
        {
            if (node.IsDirectory)
            {
                var folder = new OpenFolderDialog { Title = $"Copy '{node.Name}' to folder" };
                if (folder.ShowDialog() != true)
                    return;
                destPath = Path.Combine(folder.FolderName, node.Name);
            }
            else
            {
                var save = new SaveFileDialog { Title = $"Copy '{node.Name}' out", FileName = node.Name };
                if (save.ShowDialog() != true)
                    return;
                destPath = save.FileName;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore copy-out dialog failed");
            BugReporter.ReportException(ex, "Explore copy-out dialog failed");
            AddLog($"Copy-out failed: {ex.Message}");
            return;
        }

        IsExplorerBusy = true;
        ExplorerStatusText = $"Copying {node.FullPath}...";
        try
        {
            await Task.Run(() => explorer.CopyOut(node.FullPath, destPath)).ConfigureAwait(false);
            OnUi(() =>
            {
                ExplorerStatusText = $"Copied to {destPath}.";
                AddLog($"Explore copy-out: {node.FullPath} -> {destPath}.");
                Log.Information("Explore copy-out {Internal} -> {Dest}", node.FullPath, destPath);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore copy-out failed for {Path}", node.FullPath);
            BugReporter.ReportException(ex, $"Explore copy-out failed for {node.FullPath}");
            OnUi(() =>
            {
                ExplorerStatusText = $"Copy-out failed: {ex.Message}";
                AddLog($"Explore copy-out failed: {ex.Message}");
            });
        }
        finally
        {
            OnUi(() => IsExplorerBusy = false);
        }
    }

    private async Task HashNodeAsync()
    {
        var node = SelectedExplorerNode;
        var explorer = _explorer;
        if (node?.IsDummy != false || explorer is null)
        {
            AddLog("Select a file or directory in the explore tree first.");
            return;
        }

        if (node.IsDirectory)
        {
            AddLog("Select a file to hash (directories cannot be hashed).");
            return;
        }

        if (IsExplorerBusy)
            return;

        IsExplorerBusy = true;
        ExplorerStatusText = $"Hashing {node.FullPath}...";
        try
        {
            var hex = await Task.Run(() => explorer.ComputeHashHex(node.FullPath, HashAlgorithmName.SHA256))
                .ConfigureAwait(false);
            OnUi(() =>
            {
                ExplorerHashText = $"SHA-256({node.FullPath}) = {hex}";
                ExplorerStatusText = "Hash complete.";
                AddLog($"Explore hash: {node.FullPath} = {hex}.");
                Log.Information("Explore hash {Path} = {Hash}", node.FullPath, hex);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore hash failed for {Path}", node.FullPath);
            BugReporter.ReportException(ex, $"Explore hash failed for {node.FullPath}");
            OnUi(() =>
            {
                ExplorerStatusText = $"Hash failed: {ex.Message}";
                AddLog($"Explore hash failed: {ex.Message}");
            });
        }
        finally
        {
            OnUi(() => IsExplorerBusy = false);
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No dispatcher (unit tests, shutdown, design-time): execute synchronously
            // on the calling thread. Callers only reach here from the UI/test thread in
            // these contexts; pool-thread updates always marshal via Dispatcher.Invoke
            // above when a dispatcher exists, so bound ObservableCollections are never
            // mutated from a pool thread (TST-011).
            action();
            return;
        }

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
