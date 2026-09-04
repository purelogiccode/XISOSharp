using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using XISOSharpTester.Logging;
using XISOSharpTester.Services;
using XISOSharpTester.Views;
using XISOSharpTester.Models;

namespace XISOSharpTester.ViewModels;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// View-model for the Tester main page. Manages the extract-xiso path, selected ISO list,
/// test execution via <see cref="Services.XisoTestRunner"/>, progress, log, and results export.
/// </summary>
internal class MainViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// Initializes commands and auto-detects a sibling extract-xiso.exe.
    /// </summary>
    internal MainViewModel()
    {
        BrowseXisoSharpCommand = new RelayCommand(_ => BrowseXisoSharp());
        AddFilesCommand = new RelayCommand(_ => AddFiles());
        AddFolderCommand = new RelayCommand(_ => AddFolder());
        RemoveFileCommand = new RelayCommand(RemoveFile);
        RunTestsCommand = new RelayCommand(o => o = RunTestsAsync(), _ => CanRunTests);
        ExportPdfCommand = new RelayCommand(_ => ExportPdf(), _ => HasResults);
        CopyLogCommand = new RelayCommand(_ => CopyLog());
        CopyResultsCommand = new RelayCommand(_ => CopyResults(), _ => HasResults);
        AboutCommand = new RelayCommand(static _ => ShowAbout());
        ExitCommand = new RelayCommand(static _ => ExitApp());

        AutoDetectXisoSharp();
    }

    private void AutoDetectXisoSharp()
    {
        try
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "extract-xiso.exe");
            if (File.Exists(candidate))
            {
                XisoSharpPath = candidate;
                Log.Information("Auto-detected extract-xiso: {Path}", candidate);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-detect extract-xiso failed");
            BugReporter.ReportException(ex, "Auto-detect extract-xiso failed");
        }
    }

    private string _xisoSharpPath = string.Empty;

    /// <summary>
    /// Gets or sets the full path to extract-xiso.exe used for comparison tests.
    /// </summary>
    public string XisoSharpPath
    {
        get => _xisoSharpPath;
        set
        {
            _xisoSharpPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsXisoSharpValid));
            OnPropertyChanged(nameof(CanRunTests));
        }
    }

    /// <summary>
    /// Gets whether <see cref="XisoSharpPath"/> points to an existing executable.
    /// </summary>
    public bool IsXisoSharpValid => !string.IsNullOrEmpty(XisoSharpPath) && File.Exists(XisoSharpPath);

    /// <summary>
    /// Gets the selected XISO files to test.
    /// </summary>
    public ObservableCollection<XisoFileEntry> Files { get; } = [];

    private string _filesSummary = "No files selected.";

    /// <summary>
    /// Gets or sets the human-readable file count/total-size summary.
    /// </summary>
    public string FilesSummary
    {
        get => _filesSummary;
        set
        {
            _filesSummary = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the browse-for-extract-xiso command.</summary>
    public ICommand BrowseXisoSharpCommand { get; }

    /// <summary>Gets the add-files command.</summary>
    public ICommand AddFilesCommand { get; }

    /// <summary>Gets the add-folder (recursive ISO scan) command.</summary>
    public ICommand AddFolderCommand { get; }

    /// <summary>Gets the remove-file command.</summary>
    public ICommand RemoveFileCommand { get; }

    /// <summary>Gets the run-tests command.</summary>
    public ICommand RunTestsCommand { get; }

    /// <summary>Gets the export-PDF command.</summary>
    public ICommand ExportPdfCommand { get; }

    /// <summary>Gets the copy-log command.</summary>
    public ICommand CopyLogCommand { get; }

    /// <summary>Gets the copy-results command.</summary>
    public ICommand CopyResultsCommand { get; }

    /// <summary>Gets the show-about command.</summary>
    public ICommand AboutCommand { get; }

    /// <summary>Gets the exit-application command.</summary>
    public ICommand ExitCommand { get; }

    /// <summary>
    /// Gets whether tests can start (files selected and no run in progress).
    /// </summary>
    public bool CanRunTests => Files.Count > 0 && !IsRunning;

    private bool _isRunning;

    /// <summary>
    /// Gets or sets whether a test run is in progress.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRunTests));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(ShowResults));
        }
    }

    /// <summary>Gets whether the progress bar should be shown.</summary>
    public bool ShowProgress => IsRunning;

    /// <summary>Gets whether the results view should be shown.</summary>
    public bool ShowResults => !IsRunning && HasResults;

    private double _progressValue;

    /// <summary>Gets or sets the 0-100 progress value.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    private string _statusText = "Ready.";

    /// <summary>Gets or sets the status-bar text.</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    private string _progressText = "Ready.";

    /// <summary>Gets or sets the progress detail text.</summary>
    public string ProgressText
    {
        get => _progressText;
        set
        {
            _progressText = value;
            OnPropertyChanged();
        }
    }

    private string _currentTest = string.Empty;

    /// <summary>Gets or sets the current sub-test name (e.g. "Verify", "List").</summary>
    public string CurrentTest
    {
        get => _currentTest;
        set
        {
            _currentTest = value;
            OnPropertyChanged();
        }
    }

    private string _fileProgress = string.Empty;

    /// <summary>Gets or sets the "File i/N" progress text.</summary>
    public string FileProgress
    {
        get => _fileProgress;
        set
        {
            _fileProgress = value;
            OnPropertyChanged();
        }
    }

    private string _logText = string.Empty;

    /// <summary>Gets or sets the full scrolling log text.</summary>
    public string LogText
    {
        get => _logText;
        set
        {
            _logText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the timestamped log entries bound to the log view.</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    private TestSessionResult? _sessionResult;

    /// <summary>
    /// Gets or sets the completed session result; setting it refreshes summary bindings.
    /// </summary>
    public TestSessionResult? SessionResult
    {
        get => _sessionResult;
        set
        {
            _sessionResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(SummaryPassed));
            OnPropertyChanged(nameof(SummaryFailed));
            OnPropertyChanged(nameof(SummarySkipped));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ShowResults));
        }
    }

    /// <summary>Gets whether a completed session with file results exists.</summary>
    public bool HasResults => SessionResult is { FileResults.Count: > 0 };

    /// <summary>Gets the number of fully passing files in the session.</summary>
    public int SummaryPassed => SessionResult?.PassedFiles ?? 0;

    /// <summary>Gets the number of failed files in the session.</summary>
    public int SummaryFailed => SessionResult?.FailedFiles ?? 0;

    /// <summary>Gets the number of skipped files in the session.</summary>
    public int SummarySkipped => SessionResult?.SkippedFiles ?? 0;

    /// <summary>Gets the one-line session summary (files, sub-test counts, elapsed).</summary>
    public string SummaryText => SessionResult != null
        ? $"{SessionResult.TotalFiles} files tested | " +
          $"{SessionResult.PassedSubTests} passed, {SessionResult.FailedSubTests} failed, {SessionResult.SkippedSubTests} skipped | " +
          $"{SessionResult.TotalElapsedSeconds:N1}s total"
        : string.Empty;

    private string _summarySubText = string.Empty;

    /// <summary>Gets or sets the sub-test breakdown line shown under the summary.</summary>
    public string SummarySubText
    {
        get => _summarySubText;
        set
        {
            _summarySubText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the per-file results snapshot for binding.</summary>
    public ObservableCollection<PerFileResult> FileResults => SessionResult?.FileResults != null
        ? new ObservableCollection<PerFileResult>(SessionResult.FileResults)
        : [];

    private void BrowseXisoSharp()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select extract-xiso.exe",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = "extract-xiso.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                XisoSharpPath = dlg.FileName;
                AddLog($"extract-xiso.exe set to: {XisoSharpPath}");
                Log.Information("extract-xiso.exe set to {Path}", XisoSharpPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browse extract-xiso failed");
            BugReporter.ReportException(ex, "Browse extract-xiso failed");
            AddLog($"Error selecting extract-xiso.exe: {ex.Message}");
        }
    }

    private void AddFiles()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select XISO files", Filter = "ISO files (*.iso)|*.iso|All files (*.*)|*.*", Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var path in dlg.FileNames)
                {
                    AddFileIfNew(path);
                }

                UpdateFilesSummary();
                AddLog($"Added {dlg.FileNames.Length} file(s). Total: {Files.Count}");
                Log.Information("Added {Count} file(s). Total: {Total}", dlg.FileNames.Length, Files.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add files failed");
            BugReporter.ReportException(ex, "Add files failed");
            AddLog($"Error adding files: {ex.Message}");
        }
    }

    private void AddFolder()
    {
        OpenFolderDialog dlg;
        try
        {
            dlg = new OpenFolderDialog { Title = "Select folder with ISO files" };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Add folder dialog failed");
            BugReporter.ReportException(ex, "Add folder dialog failed");
            AddLog($"Error opening folder dialog: {ex.Message}");
            return;
        }

        if (dlg.ShowDialog() == true)
        {
            try
            {
                var isoFiles = Directory.GetFiles(dlg.FolderName, "*.iso", SearchOption.AllDirectories);
                foreach (var path in isoFiles)
                {
                    AddFileIfNew(path);
                }

                UpdateFilesSummary();
                AddLog($"Added {isoFiles.Length} file(s) from folder. Total: {Files.Count}");
                Log.Information("Added {Count} file(s) from folder. Total: {Total}", isoFiles.Length, Files.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error scanning folder {Folder}", dlg.FolderName);
                BugReporter.ReportException(ex, $"Error scanning folder {dlg.FolderName}");
                AddLog($"Error scanning folder: {ex.Message}");
            }
        }
    }

    private void AddFileIfNew(string path)
    {
        try
        {
            if (!Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                Files.Add(new XisoFileEntry { FilePath = path });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AddFileIfNew failed for {Path}", path);
            BugReporter.ReportException(ex, $"AddFileIfNew failed for {path}");
            AddLog($"Error adding file: {ex.Message}");
        }
    }

    private void RemoveFile(object? param)
    {
        try
        {
            if (param is XisoFileEntry entry)
            {
                Files.Remove(entry);
                UpdateFilesSummary();
                AddLog($"Removed: {entry.FileName}. Total: {Files.Count}");
                Log.Information("Removed {File}. Total: {Total}", entry.FileName, Files.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Remove file failed");
            BugReporter.ReportException(ex, "Remove file failed");
            AddLog($"Error removing file: {ex.Message}");
        }
    }

    private void UpdateFilesSummary()
    {
        try
        {
            var totalSize = Files.Sum(static f =>
            {
                try
                {
                    return new FileInfo(f.FilePath).Length;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not stat {Path}", f.FilePath);
                    return 0;
                }
            });
            var sizeStr = totalSize switch
            {
                < 1024 => $"{totalSize} B",
                < 1024 * 1024 => $"{totalSize / 1024.0:F1} KB",
                < 1024L * 1024 * 1024 => $"{totalSize / (1024.0 * 1024):F1} MB",
                _ => $"{totalSize / (1024.0 * 1024 * 1024):F2} GB"
            };
            FilesSummary = $"{Files.Count} file(s) \u2014 {sizeStr} total";
            OnPropertyChanged(nameof(CanRunTests));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateFilesSummary failed");
            BugReporter.ReportException(ex, "UpdateFilesSummary failed");
        }
    }

    private async Task RunTestsAsync()
    {
        if (IsRunning || Files.Count == 0) return;

        IsRunning = true;
        StatusText = "Please wait... Processing...";
        SessionResult = null;
        LogEntries.Clear();
        LogText = string.Empty;
        ProgressValue = 0;
        ProgressText = "Starting tests...";
        FileProgress = "";

        var exePath = IsXisoSharpValid ? XisoSharpPath : string.Empty;
        if (!IsXisoSharpValid)
        {
            AddLog("WARNING: extract-xiso.exe not selected. Comparison tests will be skipped.");
            Log.Warning("Test run without extract-xiso.exe; comparison tests will be skipped");
            BugReporter.ReportWarning("Test run without extract-xiso.exe; comparison tests will be skipped");
        }
        else
        {
            Log.Information("Starting test run for {Count} file(s) with {Exe}", Files.Count, exePath);
        }

        var progress = new Progress<TestProgress>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                FileProgress = $"File {p.FileIndex}/{p.TotalFiles}";
                ProgressValue = p.TotalFiles > 0 ? (double)p.FileIndex / p.TotalFiles * 100 : 0;
                ProgressText = p.StatusText;
                CurrentTest = p.CurrentTest;
                if (!string.IsNullOrEmpty(p.StatusText))
                    AddLog(p.StatusText);
            });
        });

        try
        {
            var session = await XisoTestRunner.RunAsync(Files.ToList(), exePath, progress).ConfigureAwait(false);
            SessionResult = session;

            ProgressValue = 100;
            ProgressText =
                $"Completed: {session.PassedFiles} passed, {session.FailedFiles} failed, {session.SkippedFiles} skipped";
            CurrentTest = "Done";
            StatusText =
                $"Completed: {session.PassedFiles} passed, {session.FailedFiles} failed, {session.SkippedFiles} skipped";

            SummarySubText = $"Sub-tests: {session.PassedSubTests} passed, {session.FailedSubTests} failed, " +
                             $"{session.SkippedSubTests} skipped | {session.TotalElapsedSeconds:N1}s";

            OnPropertyChanged(nameof(FileResults));
        }
        catch (Exception ex)
        {
            AddLog($"FATAL ERROR: {ex.Message}");
            Log.Error(ex, "Test run failed");
            BugReporter.ReportException(ex, "Test run failed");
            ProgressText = "Test run failed.";
            StatusText = "Error: Test run failed.";
        }
        finally
        {
            IsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ExportPdf()
    {
        if (SessionResult == null) return;

        SaveFileDialog dlg;
        try
        {
            dlg = new SaveFileDialog
            {
                Title = "Export Results to PDF",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = $"XISOSharpTester_Results_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PDF save dialog failed");
            BugReporter.ReportException(ex, "PDF save dialog failed");
            AddLog($"PDF export failed: {ex.Message}");
            return;
        }

        if (dlg.ShowDialog() == true)
        {
            try
            {
                PdfExporter.Export(SessionResult, XisoTestRunner.XisoSharpVersion, dlg.FileName);
                AddLog($"PDF exported: {dlg.FileName}");
                Log.Information("PDF exported to {Path}", dlg.FileName);
                MessageBox.Show($"Results exported successfully to:\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"PDF export failed: {ex.Message}");
                Log.Error(ex, "PDF export failed");
                BugReporter.ReportException(ex, "PDF export failed");
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyLog()
    {
        try
        {
            if (!string.IsNullOrEmpty(LogText))
            {
                Clipboard.SetText(LogText);
                Log.Debug("Log copied to clipboard ({Length} chars)", LogText.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy log failed");
            BugReporter.ReportException(ex, "Copy log failed");
            AddLog($"Copy log failed: {ex.Message}");
        }
    }

    private void CopyResults()
    {
        if (SessionResult == null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== XISOSharp Tester Results ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Summary: {SessionResult.TotalFiles} files | " +
                                                         $"{SessionResult.PassedSubTests} passed, {SessionResult.FailedSubTests} failed, " +
                                                         $"{SessionResult.SkippedSubTests} skipped | {SessionResult.TotalElapsedSeconds:N1}s");
            sb.AppendLine();

            foreach (var file in SessionResult.FileResults)
            {
                var status = file.AllPassed ? "PASS" : file.Failed > 0 ? "FAIL" : "SKIP";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"--- {file.FileName} ({file.FileSize}) [{status}] {file.ElapsedSeconds:N2}s ---");
                foreach (var t in file.SubTests)
                {
                    var icon = t.Status switch
                    {
                        TestStatus.Passed => "[PASS]",
                        TestStatus.Failed => "[FAIL]",
                        _ => "[SKIP]"
                    };
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  {icon} {t.TestName,-22} {t.ElapsedSeconds,6:N2}s  {t.Detail}");
                }

                sb.AppendLine();
            }

            Clipboard.SetText(sb.ToString());
            Log.Debug("Results copied to clipboard");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy results failed");
            BugReporter.ReportException(ex, "Copy results failed");
            AddLog($"Copy results failed: {ex.Message}");
        }
    }

    private void AddLog(string message)
    {
        try
        {
            var ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            LogEntries.Add(new LogEntry { Message = message, Timestamp = ts });
            LogText += $"[{ts}] {message}\n";
            if (message.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                Log.Error("{TesterLog}", message);
            else if (message.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
                Log.Warning("{TesterLog}", message);
            else
                Log.Information("{TesterLog}", message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddLog failed: {ex.Message}");
        }
    }

    private static void ShowAbout()
    {
        try
        {
            var about = new AboutWindow { Owner = Application.Current.MainWindow };
            about.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ShowAbout failed");
            BugReporter.ReportException(ex, "ShowAbout failed");
        }
    }

    private static void ExitApp()
    {
        try
        {
            Log.Information("Exit requested");
            Application.Current.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exit failed");
            BugReporter.ReportException(ex, "Exit failed");
        }
    }

    /// <summary>
    /// Occurs when a bound property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for the caller property.
    /// </summary>
    /// <param name="name">Property name; defaults to the caller member name.</param>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Represents a single log entry displayed in the application's
/// scrolling log output, with a message and timestamp.
/// </summary>
public class LogEntry
{
    /// <summary>
    /// Gets or sets the log message text.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp string (e.g. "14:30:05").
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// A generic command implementation for WPF that delegates
/// its execution and can-execute logic to callbacks. Also
/// wires the <c>CommandManager.RequerySuggested</c> event.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// Creates a new <see cref="RelayCommand"/>.
    /// </summary>
    /// <param name="execute">The delegate to invoke when the command is executed.</param>
    /// <param name="canExecute">
    /// Optional delegate that determines whether the command can execute.
    /// If <c>null</c>, the command is always enabled.
    /// </param>
    internal RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>
    /// Determines whether the command can execute in its current state.
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke(parameter) ?? true;
    }

    /// <summary>
    /// Invokes the execute delegate.
    /// </summary>
    public void Execute(object? parameter)
    {
        _execute(parameter);
    }

    /// <summary>
    /// Occurs when changes in the UI state affect whether the command
    /// should execute.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}