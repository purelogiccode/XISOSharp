using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using Serilog;
using XISOSharpTester.Logging;

namespace XISOSharpTester.ViewModels;

/// <summary>
/// One row of the Explore tab's <c>TreeView</c>: wraps a library
/// <see cref="XISOSharp.ExplorerNode"/> and lazily loads directory children on
/// first expand (a dummy placeholder keeps the expander visible until then).
/// Load failures are surfaced via <see cref="ErrorText"/> instead of throwing
/// out of the binding engine.
/// </summary>
internal sealed class ExplorerTreeNode : INotifyPropertyChanged
{
    private static readonly ExplorerTreeNode Dummy = new();

    private readonly Func<ExplorerTreeNode, IReadOnlyList<XISOSharp.ExplorerNode>>? _loader;
    private ObservableCollection<ExplorerTreeNode>? _children;
    private bool _isExpanded;
    private bool _isSelected;
    private string? _errorText;
    private bool _isLoading;

    private ExplorerTreeNode()
    {
        Name = "...";
        FullPath = string.Empty;

        // Stable empty list so binding to the shared placeholder never allocates
        // or mutates shared state (TST-010). Dummy is immutable: never expanded,
        // never selected (see guards), never reloaded.
        _children = [];
    }

    /// <summary>
    /// Wraps <paramref name="data"/>; <paramref name="loader"/> lists children
    /// by node on first expand (directories only).
    /// </summary>
    internal ExplorerTreeNode(
        XISOSharp.ExplorerNode data,
        Func<ExplorerTreeNode, IReadOnlyList<XISOSharp.ExplorerNode>> loader)
    {
        Name = data.Name;
        FullPath = data.FullPath;
        IsDirectory = data.IsDirectory;
        Size = data.Size;
        StartSector = data.StartSector;
        Attributes = data.Attributes;
        _loader = loader;
        if (data.IsDirectory)
            _children = [Dummy];
    }

    /// <summary>Gets the entry file name.</summary>
    public string Name { get; }

    /// <summary>Gets the image-internal path.</summary>
    public string FullPath { get; }

    /// <summary>Gets whether this node is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the file byte size (0 for directories).</summary>
    public long Size { get; }

    /// <summary>Gets the partition-relative first sector.</summary>
    public uint StartSector { get; }

    /// <summary>Gets the raw attribute byte.</summary>
    public byte Attributes { get; }

    /// <summary>Gets the human-readable kind/size suffix shown in the tree.</summary>
    public string Suffix => IsDirectory ? "dir" : FormatSize(Size);

    /// <summary>Gets the child rows (dummy placeholder until first expand).</summary>
    public ObservableCollection<ExplorerTreeNode> Children => _children ??= [];

    /// <summary>Gets whether this is the expand placeholder rather than a real entry.</summary>
    public bool IsDummy => ReferenceEquals(this, Dummy);

    /// <summary>
    /// Gets or sets whether the tree row is expanded; expanding a directory for
    /// the first time loads its children off the UI thread (TST-010).
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (IsDummy)
                return;

            _isExpanded = value;
            OnPropertyChanged();
            if (value)
                EnsureChildren();
        }
    }

    /// <summary>Gets or sets whether the tree row is selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsDummy)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the child-load error, if the last expand failed.</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            _errorText = value;
            OnPropertyChanged();
        }
    }

    private void EnsureChildren()
    {
        if (IsDummy || !IsDirectory || _loader is null || _children is null)
            return;

        if (_children.Count != 1 || !_children[0].IsDummy)
            return;

        if (_isLoading)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No dispatcher (unit tests, shutdown, design-time): load synchronously
            // on the calling (test/UI) thread so the bound collection is never
            // mutated from a pool thread (TST-010/TST-011).
            try
            {
                var syncLoaded = _loader(this);
                _children.Clear();
                foreach (var child in syncLoaded)
                    _children.Add(new ExplorerTreeNode(child, _loader));

                ErrorText = null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Explore expand failed for {Path}", FullPath);
                BugReporter.ReportException(ex, $"Explore expand failed for {FullPath}");
                _children.Clear();
                ErrorText = $"Could not list {FullPath}: {ex.Message}";
            }

            return;
        }

        _isLoading = true;

        // Faults are observed inside (top-level try/catch surfacing to the log),
        // so the discarded operation never faults (TST-003: no unobserved Task).
        _ = EnsureChildrenAsync(dispatcher);
    }

    private async Task EnsureChildrenAsync(System.Windows.Threading.Dispatcher dispatcher)
    {
        var loader = _loader;
        var self = this;
        IReadOnlyList<XISOSharp.ExplorerNode> loaded;
        try
        {
            // Off the UI thread: ListChildren performs sync I/O (TST-010).
            loaded = await Task.Run(() => loader!(self)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Explore expand failed for {Path}", FullPath);
            BugReporter.ReportException(ex, $"Explore expand failed for {FullPath}");
            MarshalExpandResult(dispatcher, null, $"Could not list {FullPath}: {ex.Message}");
            return;
        }

        MarshalExpandResult(dispatcher, loaded, null);
    }

    private void MarshalExpandResult(System.Windows.Threading.Dispatcher dispatcher, IReadOnlyList<XISOSharp.ExplorerNode>? loaded, string? error)
    {
        try
        {
            dispatcher.Invoke(() =>
            {
                try
                {
                    if (_children is null)
                    {
                        _isLoading = false;
                        return;
                    }

                    // Parent may have been closed or refreshed while loading;
                    // only replace the placeholder, never clobber fresh children.
                    if (_children.Count == 1 && _children[0].IsDummy)
                    {
                        _children.Clear();
                        if (loaded is not null)
                        {
                            var loader = _loader;
                            if (loader is not null)
                            {
                                foreach (var child in loaded)
                                    _children.Add(new ExplorerTreeNode(child, loader));
                            }
                        }
                    }

                    ErrorText = error;
                    _isLoading = false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Explore expand marshal failed for {Path}", FullPath);
                    _isLoading = false;
                }
            });
        }
        catch (Exception ex)
        {
            // Dispatcher shut down between load and marshal: best-effort, already logged.
            Log.Warning(ex, "Explore expand invoke failed for {Path}", FullPath);
            _isLoading = false;
        }
    }

    /// <summary>Formats a byte count for tree/detail display.</summary>
    internal static string FormatSize(long size)
    {
        return size switch
        {
            < 1024 => $"{size.ToString(CultureInfo.InvariantCulture)} B",
            < 1024 * 1024 => $"{size / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024):F1} MB",
            _ => $"{size / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    /// <summary>Occurs when a bound property value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}