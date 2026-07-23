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
using XISOSharpTester.Models;
using XISOSharpTester.Services;

namespace XISOSharpTester.ViewModels;

internal class MainViewModel : INotifyPropertyChanged
{
    private readonly XisoTestRunner _runner = new();

    internal MainViewModel()
    {
        BrowseXISOSharpCommand = new RelayCommand(_ => BrowseXISOSharp());
        AddFilesCommand = new RelayCommand(_ => AddFiles());
        AddFolderCommand = new RelayCommand(_ => AddFolder());
        RemoveFileCommand = new RelayCommand(RemoveFile);
        RunTestsCommand = new RelayCommand(o => { _ = RunTestsAsync(); }, _ => CanRunTests);
        ExportPdfCommand = new RelayCommand(_ => ExportPdf(), _ => HasResults);
        CopyLogCommand = new RelayCommand(_ => CopyLog());
        CopyResultsCommand = new RelayCommand(_ => CopyResults(), _ => HasResults);
        AboutCommand = new RelayCommand(_ => ShowAbout());
        ExitCommand = new RelayCommand(_ => ExitApp());

        AutoDetectXISOSharp();
    }

    private void AutoDetectXISOSharp()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "extract-xiso.exe");
        if (File.Exists(candidate))
        {
            XISOSharpPath = candidate;
        }
    }

    private string _XISOSharpPath = string.Empty;
    public string XISOSharpPath
    {
        get => _XISOSharpPath;
        set { _XISOSharpPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsXISOSharpValid));
            OnPropertyChanged(nameof(CanRunTests)); }
    }

    public bool IsXISOSharpValid => !string.IsNullOrEmpty(XISOSharpPath) && File.Exists(XISOSharpPath);

    public ObservableCollection<XisoFileEntry> Files { get; } = [];

    private string _filesSummary = "No files selected.";
    public string FilesSummary
    {
        get => _filesSummary;
        set { _filesSummary = value;
            OnPropertyChanged(); }
    }

    public ICommand BrowseXISOSharpCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFileCommand { get; }
    public ICommand RunTestsCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand CopyResultsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand ExitCommand { get; }

    public bool CanRunTests => Files.Count > 0 && !IsRunning;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRunTests));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(ShowResults)); }
    }

    public bool ShowProgress => IsRunning;

    public bool ShowResults => !IsRunning && HasResults;

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value;
            OnPropertyChanged(); }
    }

    private string _statusText = "Ready.";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value;
            OnPropertyChanged(); }
    }

    private string _progressText = "Ready.";
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value;
            OnPropertyChanged(); }
    }

    private string _currentTest = string.Empty;
    public string CurrentTest
    {
        get => _currentTest;
        set { _currentTest = value;
            OnPropertyChanged(); }
    }

    private string _fileProgress = string.Empty;
    public string FileProgress
    {
        get => _fileProgress;
        set { _fileProgress = value;
            OnPropertyChanged(); }
    }

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        set { _logText = value;
            OnPropertyChanged(); }
    }

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    private TestSessionResult? _sessionResult;
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

    public bool HasResults => SessionResult is { FileResults.Count: > 0 };

    public int SummaryPassed => SessionResult?.PassedFiles ?? 0;

    public int SummaryFailed => SessionResult?.FailedFiles ?? 0;

    public int SummarySkipped => SessionResult?.SkippedFiles ?? 0;

    public string SummaryText => SessionResult != null
        ? $"{SessionResult.TotalFiles} files tested | " +
          $"{SessionResult.PassedSubTests} passed, {SessionResult.FailedSubTests} failed, {SessionResult.SkippedSubTests} skipped | " +
          $"{SessionResult.TotalElapsedSeconds:N1}s total"
        : string.Empty;

    private string _summarySubText = string.Empty;
    public string SummarySubText
    {
        get => _summarySubText;
        set { _summarySubText = value;
            OnPropertyChanged(); }
    }

    public ObservableCollection<PerFileResult> FileResults => SessionResult?.FileResults != null
        ? new ObservableCollection<PerFileResult>(SessionResult.FileResults)
        : [];

    private void BrowseXISOSharp()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select extract-xiso.exe",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = "extract-xiso.exe"
        };
        if (dlg.ShowDialog() == true)
        {
            XISOSharpPath = dlg.FileName;
            AddLog($"extract-xiso.exe set to: {XISOSharpPath}");
        }
    }

    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select XISO files",
            Filter = "ISO files (*.iso)|*.iso|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var path in dlg.FileNames)
            {
                AddFileIfNew(path);
            }
            UpdateFilesSummary();
            AddLog($"Added {dlg.FileNames.Length} file(s). Total: {Files.Count}");
        }
    }

    private void AddFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select folder with ISO files"
        };
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
            }
            catch (Exception ex)
            {
                AddLog($"Error scanning folder: {ex.Message}");
            }
        }
    }

    private void AddFileIfNew(string path)
    {
        if (!Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            Files.Add(new XisoFileEntry { FilePath = path });
        }
    }

    private void RemoveFile(object? param)
    {
        if (param is XisoFileEntry entry)
        {
            Files.Remove(entry);
            UpdateFilesSummary();
            AddLog($"Removed: {entry.FileName}. Total: {Files.Count}");
        }
    }

    private void UpdateFilesSummary()
    {
        var totalSize = Files.Sum(f => new FileInfo(f.FilePath).Length);
        var sizeStr = totalSize switch
        {
            < 1024 => $"{totalSize} B",
            < 1024 * 1024 => $"{totalSize / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{totalSize / (1024.0 * 1024):F1} MB",
            _ => $"{totalSize / (1024.0 * 1024 * 1024):F2} GB"
        };
        FilesSummary = $"{Files.Count} file(s) — {sizeStr} total";
        OnPropertyChanged(nameof(CanRunTests));
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

        var exePath = IsXISOSharpValid ? XISOSharpPath : string.Empty;
        if (!IsXISOSharpValid)
        {
            AddLog("WARNING: extract-xiso.exe not selected. Comparison tests will be skipped.");
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
            var session = await _runner.RunAsync(Files.ToList(), exePath, progress);
            SessionResult = session;

            ProgressValue = 100;
            ProgressText = $"Completed: {session.PassedFiles} passed, {session.FailedFiles} failed, {session.SkippedFiles} skipped";
            CurrentTest = "Done";
            StatusText = $"Completed: {session.PassedFiles} passed, {session.FailedFiles} failed, {session.SkippedFiles} skipped";

            SummarySubText = $"Sub-tests: {session.PassedSubTests} passed, {session.FailedSubTests} failed, " +
                             $"{session.SkippedSubTests} skipped | {session.TotalElapsedSeconds:N1}s";

            OnPropertyChanged(nameof(FileResults));
        }
        catch (Exception ex)
        {
            AddLog($"FATAL ERROR: {ex.Message}");
            Log.Error(ex, "Test run failed");
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

        var dlg = new SaveFileDialog
        {
            Title = "Export Results to PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"XISOSharpTester_Results_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                PdfExporter.Export(SessionResult, _runner.XISOSharpVersion, dlg.FileName);
                AddLog($"PDF exported: {dlg.FileName}");
                MessageBox.Show($"Results exported successfully to:\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"PDF export failed: {ex.Message}");
                Log.Error(ex, "PDF export failed");
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyLog()
    {
        if (!string.IsNullOrEmpty(LogText))
            Clipboard.SetText(LogText);
    }

    private void CopyResults()
    {
        if (SessionResult == null) return;

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
            sb.AppendLine(CultureInfo.InvariantCulture, $"--- {file.FileName} ({file.FileSize}) [{status}] {file.ElapsedSeconds:N2}s ---");
            foreach (var t in file.SubTests)
            {
                var icon = t.Status switch
                {
                    TestStatus.Passed => "[PASS]",
                    TestStatus.Failed => "[FAIL]",
                    _ => "[SKIP]"
                };
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {icon} {t.TestName,-22} {t.ElapsedSeconds,6:N2}s  {t.Detail}");
            }
            sb.AppendLine();
        }

        Clipboard.SetText(sb.ToString());
    }

    private void AddLog(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        LogEntries.Add(new LogEntry { Message = message, Timestamp = ts });
        LogText += $"[{ts}] {message}\n";
    }

    private static void ShowAbout()
    {
        var about = new Views.AboutWindow
        {
            Owner = Application.Current.MainWindow
        };
        about.ShowDialog();
    }

    private static void ExitApp()
    {
        Application.Current.MainWindow?.Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class LogEntry
{
    public string Message { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    internal RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke(parameter) ?? true;
    }

    public void Execute(object? parameter)
    {
        _execute(parameter);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
