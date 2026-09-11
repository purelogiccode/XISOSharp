using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOSharpTester.Logging;
using XISOSharpTester.Models;

namespace XISOSharpTester.Services;

/// <summary>
/// Orchestrates batch testing of XISO disc images by running
/// verify, list, extract, and rewrite comparisons against both
/// the managed XISOSharp library and the native extract-xiso tool
/// (extract-xiso.exe on Windows, extensionless elsewhere) when available.
/// </summary>
public static class XisoTestRunner
{
    private static readonly Lock LoggerLock = new();

    /// <summary>
    /// Gets the version string of the extract-xiso tool executable,
    /// if it was successfully detected during the last run.
    /// </summary>
    public static string? XisoSharpVersion { get; private set; }

    /// <summary>
    /// Runs the full test suite against all specified XISO files,
    /// reporting progress to an optional <paramref name="progress"/>
    /// callback.
    /// </summary>
    /// <param name="files">The list of XISO entries to test.</param>
    /// <param name="xisoSharpExePath">
    /// Path to the extract-xiso tool (any executable spelling resolved via
    /// <c>XISOSharp.ToolLocator</c>). If the file does not exist,
    /// comparison tests against the native tool are skipped.
    /// </param>
    /// <param name="progress">Optional progress reporter invoked after each file.</param>
    /// <param name="cancellationToken">Cancels the session between files and inside sub-tests.</param>
    /// <returns>A <see cref="TestSessionResult"/> aggregating all per-file results.</returns>
    public static async Task<TestSessionResult> RunAsync(
        IList<XisoFileEntry> files,
        string xisoSharpExePath,
        IProgress<TestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(files);
            Log.Information("Test session starting for {Count} file(s)", files.Count);
            TestSessionResult session = new();
            bool exeAvailable = File.Exists(xisoSharpExePath);

            using XisoSharpWrapper? wrapper = exeAvailable ? new XisoSharpWrapper(xisoSharpExePath) : null;

            if (wrapper != null)
            {
                try
                {
                    XisoSharpVersion = await wrapper.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "GetVersion failed");
                    BugReporter.ReportException(ex, "GetVersion failed");
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                XisoFileEntry file = files[i];
                int fileIndex = i;
                XisoSharpWrapper? currentWrapper = wrapper;
                progress?.Report(new TestProgress(file.FileName, fileIndex + 1, files.Count,
                    "Starting", $"Testing {file.FileName}..."));

                PerFileResult result = await Task
                    .Run(() => TestSingleFile(file, currentWrapper, progress, fileIndex, files.Count,
                        cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                session.FileResults.Add(result);
            }

            progress?.Report(new TestProgress("Done", files.Count, files.Count,
                "Complete", "All tests finished.", true));

            Log.Information("Test session finished: {Passed} passed, {Failed} failed, {Skipped} skipped",
                session.PassedFiles, session.FailedFiles, session.SkippedFiles);
            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Test session failed");
            BugReporter.ReportException(ex, "Test session failed");
            throw;
        }
    }

    private static async Task<PerFileResult> TestSingleFile(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        PerFileResult result = new()
        {
            FileName = entry.FileName, FilePath = entry.FilePath, FileSize = entry.FileSize
        };

        try
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.FilePath;

            if (!File.Exists(path))
            {
                Log.Warning("Test skipped (file not found): {Path}", path);
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "All Tests", Status = TestStatus.Skipped, Detail = "File not found on disk."
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            // Test 1: Verify XISO header
            await RunVerifyTest(entry, wrapper, progress, fileIndex, totalFiles, result, cancellationToken)
                .ConfigureAwait(false);

            // Test 2: List files comparison
            await RunListTest(entry, wrapper, progress, fileIndex, totalFiles, result, cancellationToken)
                .ConfigureAwait(false);

            // Test 3: Extract all files & hash comparison
            await RunExtractTest(entry, wrapper, progress, fileIndex, totalFiles, result, cancellationToken)
                .ConfigureAwait(false);

            // Test 4: Rewrite comparison
            await RunRewriteTest(entry, wrapper, progress, fileIndex, totalFiles, result, cancellationToken)
                .ConfigureAwait(false);

            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            Log.Information("[{Status}] {File} ({Time:N1}s)",
                result.AllPassed ? "PASS" : "FAIL", entry.FileName, result.ElapsedSeconds);
            if (!result.AllPassed)
                BugReporter.ReportWarning($"Test failures for {entry.FileName}");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TestSingleFile failed for {File}", entry.FileName);
            BugReporter.ReportException(ex, $"TestSingleFile failed for {entry.FileName}");
            result.SubTests.Add(new SubTestResult
            {
                TestName = "All Tests",
                Status = TestStatus.Failed,
                Detail = $"Harness error: {ex.Message}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            });
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }
    }

    private static async Task RunVerifyTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result,
        CancellationToken cancellationToken = default)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Verify",
            "Validating XISO header...");

        Stopwatch tSw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream fs = File.OpenRead(entry.FilePath);
            (uint rootDirSector, uint rootDirSize, long discLseek) = XisoReader.VerifyXiso(fs, entry.FileName);
            tSw.Stop();

            string csDetail = $"Valid XISO | RootSector={rootDirSector} RootSize={rootDirSize} DiscLseek={discLseek}";

            if (wrapper is { Available: true })
            {
                XisoSharpWrapper.Result exeResult = await wrapper.ListFilesAsync(entry.FilePath, cancellationToken)
                    .ConfigureAwait(false);
                bool exeValid = exeResult.ExitCode == 0;
                string exeDetail = exeValid ? "extract-xiso: valid" : $"extract-xiso: exit code {exeResult.ExitCode}";

                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Verify XISO",
                    Status = exeValid ? TestStatus.Passed : TestStatus.Failed,
                    Detail = $"C#: {csDetail}\nextract-xiso: {exeDetail}",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
            }
            else
            {
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Verify XISO",
                    Status = TestStatus.Passed,
                    Detail = csDetail,
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
            }
        }
        catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
        {
            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Verify XISO",
                Status = TestStatus.Skipped,
                Detail = $"Empty XISO (no files): {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tSw.Stop();
            Log.Error(ex, "Verify test failed for {File}", entry.FileName);
            BugReporter.ReportException(ex, $"Verify test failed for {entry.FileName}");
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Verify XISO",
                Status = TestStatus.Failed,
                Detail = $"C# error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static async Task RunListTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result,
        CancellationToken cancellationToken = default)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "List",
            "Comparing file listing...");

        Stopwatch tSw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Get C# listing
            string csOutput = CaptureCSharpListOutput(entry.FilePath, cancellationToken);
            List<ListEntry> csEntries = ParseListOutput(csOutput);

            if (wrapper is { Available: true })
            {
                XisoSharpWrapper.Result exeResult = await wrapper.ListFilesAsync(entry.FilePath, cancellationToken)
                    .ConfigureAwait(false);
                if (exeResult.ExitCode == 0)
                {
                    List<ListEntry> exeEntries = ParseListOutput(exeResult.StdOut);
                    ListComparison comparison = CompareListEntries(csEntries, exeEntries);
                    tSw.Stop();

                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "List Files",
                        Status = comparison.AllMatch ? TestStatus.Passed : TestStatus.Failed,
                        Detail = comparison.Detail,
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                }
                else
                {
                    tSw.Stop();
                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "List Files",
                        Status = TestStatus.Failed,
                        Detail = $"extract-xiso list failed (exit {exeResult.ExitCode})",
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                }
            }
            else if (csEntries.Count > 0)
            {
                tSw.Stop();
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "List Files",
                    Status = TestStatus.Passed,
                    Detail = $"C# listing: {csEntries.Count} entries",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
            }
            else
            {
                tSw.Stop();
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "List Files",
                    Status = TestStatus.Skipped,
                    Detail = "No entries found (empty ISO?).",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tSw.Stop();
            Log.Error(ex, "List test failed for {File}", entry.FileName);
            BugReporter.ReportException(ex, $"List test failed for {entry.FileName}");
            result.SubTests.Add(new SubTestResult
            {
                TestName = "List Files",
                Status = TestStatus.Failed,
                Detail = $"Error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static async Task RunExtractTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result,
        CancellationToken cancellationToken = default)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Extract",
            "Extracting and comparing file hashes...");

        Stopwatch tSw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long isoLength = TryGetIsoLength(entry.FilePath);
            if (isoLength > 0)
            {
                // Staging holds two full extractions (C# + native); gate with margin (TST-013).
                long required = (isoLength * 2) + (100L * 1024 * 1024);
                string? skip = CheckTempSpace(required, entry.FilePath);
                if (skip is not null)
                {
                    tSw.Stop();
                    Log.Warning("Extract test skipped for {File}: {Reason}", entry.FileName, skip);
                    Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Extract", $"WARNING: {skip}");
                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "Extract & Hash Compare",
                        Status = TestStatus.Skipped,
                        Detail = skip,
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                    return;
                }
            }

            string csTempDir = CreateTempSubDir("cs_extract");
            string exeTempDir = CreateTempSubDir("exe_extract");
            try
            {
                try
                {
                    lock (LoggerLock)
                    {
                        bool saveQuiet = Logger.Quiet;
                        Logger.Quiet = true;
                        try
                        {
                            XisoReader.Extract(entry.FilePath, csTempDir, false, cancellationToken);
                        }
                        finally
                        {
                            Logger.Quiet = saveQuiet;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
                {
                    tSw.Stop();
                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "Extract & Hash Compare",
                        Status = TestStatus.Skipped,
                        Detail = "Empty XISO (no files to extract).",
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                    return;
                }

                // extract-xiso extraction
                if (wrapper is { Available: true })
                {
                    XisoSharpWrapper.Result exeResult = await wrapper
                        .ExtractFilesAsync(entry.FilePath, exeTempDir, cancellationToken)
                        .ConfigureAwait(false);
                    if (exeResult.ExitCode != 0)
                    {
                        tSw.Stop();
                        result.SubTests.Add(new SubTestResult
                        {
                            TestName = "Extract & Hash Compare",
                            Status = TestStatus.Failed,
                            Detail = $"extract-xiso extraction failed (exit {exeResult.ExitCode})",
                            ElapsedSeconds = tSw.Elapsed.TotalSeconds
                        });
                        return;
                    }

                    DirComparison comparison = CompareExtractedDirs(csTempDir, exeTempDir);
                    tSw.Stop();

                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "Extract & Hash Compare",
                        Status = comparison.AllMatch ? TestStatus.Passed : TestStatus.Failed,
                        Detail = comparison.Detail,
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                }
                else
                {
                    tSw.Stop();
                    int csFileCount = CountFiles(csTempDir);
                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "Extract & Hash Compare",
                        Status = TestStatus.Passed,
                        Detail = $"C# extraction: {csFileCount} files (extract-xiso not available for comparison)",
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                }
            }
            finally
            {
                DeleteDirectorySafe(csTempDir, progress, entry.FileName, fileIndex + 1, totalFiles, "Extract");
                DeleteDirectorySafe(exeTempDir, progress, entry.FileName, fileIndex + 1, totalFiles, "Extract");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tSw.Stop();
            Log.Error(ex, "Extract test failed for {File}", entry.FileName);
            BugReporter.ReportException(ex, $"Extract test failed for {entry.FileName}");
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Extract & Hash Compare",
                Status = TestStatus.Failed,
                Detail = $"Error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static async Task RunRewriteTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result,
        CancellationToken cancellationToken = default)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Rewrite",
            "Rewriting and comparing ISO hashes...");

        if (wrapper is not { Available: true })
        {
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Rewrite Compare",
                Status = TestStatus.Skipped,
                Detail = "extract-xiso tool not available."
            });
            return;
        }

        Stopwatch tSw = Stopwatch.StartNew();
        long isoLength = TryGetIsoLength(entry.FilePath);
        if (isoLength > 0)
        {
            // Rewrite stages an input copy plus a rebuilt ISO per side (TST-013).
            long required = (isoLength * 4) + (100L * 1024 * 1024);
            string? skip = CheckTempSpace(required, entry.FilePath);
            if (skip is not null)
            {
                tSw.Stop();
                Log.Warning("Rewrite test skipped for {File}: {Reason}", entry.FileName, skip);
                Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Rewrite", $"WARNING: {skip}");
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Rewrite Compare",
                    Status = TestStatus.Skipped,
                    Detail = skip,
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
                return;
            }
        }

        string csWorkDir = CreateTempSubDir("cs_rewrite");
        string exeWorkDir = CreateTempSubDir("exe_rewrite");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // C# rewrite
            string csInput = Path.Combine(csWorkDir, entry.FileName);
            File.Copy(entry.FilePath, csInput, true);

            // Check if already optimized (tag at offset 31337)
            await using (FileStream fs = File.OpenRead(csInput))
            {
                if (fs.Length > Constants.OptimizedTagOffset + Constants.OptimizedTag.Length)
                {
                    fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                    Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTag.Length];
                    fs.ReadExactly(tagBuf);
                    string tag = Encoding.ASCII.GetString(tagBuf);
                    if (string.Equals(tag, Constants.OptimizedTag, StringComparison.Ordinal))
                    {
                        tSw.Stop();
                        result.SubTests.Add(new SubTestResult
                        {
                            TestName = "Rewrite Compare",
                            Status = TestStatus.Skipped,
                            Detail = "XISO already optimized (rewrite skipped).",
                            ElapsedSeconds = tSw.Elapsed.TotalSeconds
                        });
                        return;
                    }
                }
            }

            string csOutDir = Path.Combine(csWorkDir, "cs_out");
            Directory.CreateDirectory(csOutDir);

            lock (LoggerLock)
            {
                bool saveQuiet = Logger.Quiet;
                Logger.Quiet = true;
                try
                {
                    XisoReader.Rewrite(csInput, csOutDir, out _, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ExtractErrorException ex) when (
                    ex.ErrorCode is ExtractError.ErrIsoRewritten or ExtractError.ErrIsoNoFiles)
                {
                    tSw.Stop();
                    result.SubTests.Add(new SubTestResult
                    {
                        TestName = "Rewrite Compare",
                        Status = TestStatus.Skipped,
                        Detail = $"Skipped: {ex.Message}",
                        ElapsedSeconds = tSw.Elapsed.TotalSeconds
                    });
                    return;
                }
                finally
                {
                    Logger.Quiet = saveQuiet;
                }
            }

            // Find the C# output ISO
            // Maybe in csWorkDir
            string? csIsoOutput = Directory.GetFiles(csOutDir, "*.iso").FirstOrDefault() ?? Directory
                .GetFiles(csWorkDir, "*.iso", SearchOption.AllDirectories)
                .FirstOrDefault(static f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase));

            // extract-xiso rewrite
            string exeInput = Path.Combine(exeWorkDir, entry.FileName);
            File.Copy(entry.FilePath, exeInput, true);

            string exeOutDir = Path.Combine(exeWorkDir, "exe_out");
            Directory.CreateDirectory(exeOutDir);

            XisoSharpWrapper.Result exeResult = await wrapper.RewriteAsync(exeInput, exeOutDir, cancellationToken)
                .ConfigureAwait(false);
            if (exeResult.ExitCode != 0)
            {
                tSw.Stop();
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Rewrite Compare",
                    Status = TestStatus.Failed,
                    Detail = $"extract-xiso rewrite failed (exit {exeResult.ExitCode})",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
                return;
            }

            string? exeIsoOutput = Directory.GetFiles(exeOutDir, "*.iso").FirstOrDefault();

            if (csIsoOutput == null || exeIsoOutput == null)
            {
                tSw.Stop();
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Rewrite Compare",
                    Status = TestStatus.Failed,
                    Detail =
                        $"Could not locate output ISOs. C#: {csIsoOutput ?? "null"} | exe: {exeIsoOutput ?? "null"}",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
                return;
            }

            string csHash = HashUtil.ComputeSha256(csIsoOutput);
            string exeHash = HashUtil.ComputeSha256(exeIsoOutput);
            bool match = string.Equals(csHash, exeHash, StringComparison.Ordinal);
            long csSize = new FileInfo(csIsoOutput).Length;
            long exeSize = new FileInfo(exeIsoOutput).Length;

            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Rewrite Compare",
                Status = match ? TestStatus.Passed : TestStatus.Failed,
                Detail = match
                    ? $"SHA-256: {csHash} \u2713 ({csSize / (1024.0 * 1024):F1} MB vs {exeSize / (1024.0 * 1024):F1} MB)"
                    : $"SHA-256 MISMATCH\nC#:      {csHash} ({csSize / (1024.0 * 1024):F1} MB)\nextract-xiso: {exeHash} ({exeSize / (1024.0 * 1024):F1} MB)",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tSw.Stop();
            Log.Error(ex, "Rewrite test failed for {File}", entry.FileName);
            BugReporter.ReportException(ex, $"Rewrite test failed for {entry.FileName}");
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Rewrite Compare",
                Status = TestStatus.Failed,
                Detail = $"Error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
        finally
        {
            DeleteDirectorySafe(csWorkDir, progress, entry.FileName, fileIndex + 1, totalFiles, "Rewrite");
            DeleteDirectorySafe(exeWorkDir, progress, entry.FileName, fileIndex + 1, totalFiles, "Rewrite");
        }
    }

    private static string CaptureCSharpListOutput(string isoPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (LoggerLock)
        {
            TextWriter saveOut = Logger.Out;
            bool saveQuiet = Logger.Quiet;
            using StringWriter sw = new(CultureInfo.InvariantCulture);
            Logger.Out = sw;
            Logger.Quiet = false;
            try
            {
                XisoReader.List(isoPath, false, cancellationToken);
                sw.Flush();
                return sw.ToString();
            }
            catch (ExtractErrorException)
            {
                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CaptureCSharpListOutput failed for {Iso}", isoPath);
                BugReporter.ReportException(ex, $"CaptureCSharpListOutput failed for {isoPath}");
                return string.Empty;
            }
            finally
            {
                Logger.Out = saveOut;
                Logger.Quiet = saveQuiet;
            }
        }
    }

    private sealed record ListEntry(
        string Path,
        bool IsDirectory,
        uint Size,
        uint StartSector);

    private static List<ListEntry> ParseListOutput(string output)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(output);
            List<ListEntry> entries = new();

            // Matches: " - Path: filename                        Size: N bytes,  StartSector: S"
            // or with nesting: " - filename                        Size: N bytes,  StartSector: S"
            // or dir: " - dirname                                 DIR"
            Regex fileRegex =
                new(@"^\s*-\s+(?<path>.*?)\s{2,}Size:\s*(?<size>\d+)\s*bytes,\s*StartSector:\s*(?<sector>\d+)",
                    RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(5));
            Regex dirRegex = new(@"^\s*-\s+(?<path>.*?)\s{2,}DIR", RegexOptions.Multiline | RegexOptions.Compiled,
                TimeSpan.FromSeconds(5));

            foreach (Match m in fileRegex.Matches(output))
            {
                string path = m.Groups["path"].Value.Trim();
                uint size = uint.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
                uint sector = uint.Parse(m.Groups["sector"].Value, CultureInfo.InvariantCulture);
                entries.Add(new ListEntry(path, false, size, sector));
            }

            foreach (Match m in dirRegex.Matches(output))
            {
                string path = m.Groups["path"].Value.Trim();
                entries.Add(new ListEntry(path, true, 0, 0));
            }

            entries.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return entries;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ParseListOutput failed");
            BugReporter.ReportException(ex, "ParseListOutput failed");
            return [];
        }
    }

    private sealed record ListComparison(bool AllMatch, string Detail);

    private static ListComparison CompareListEntries(List<ListEntry> csEntries, List<ListEntry> exeEntries)
    {
        List<string> details = new();

        if (csEntries.Count != exeEntries.Count)
        {
            details.Add($"File count: C#={csEntries.Count} extract-xiso={exeEntries.Count}");
        }

        Dictionary<string, ListEntry> csByPath =
            csEntries.ToDictionary(static e => e.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ListEntry> exeByPath =
            exeEntries.ToDictionary(static e => e.Path, StringComparer.OrdinalIgnoreCase);

        int matchCount = 0;
        int mismatchCount = 0;

        foreach ((string path, ListEntry csEntry) in csByPath)
        {
            if (exeByPath.TryGetValue(path, out ListEntry? exeEntry))
            {
                if (csEntry.IsDirectory == exeEntry.IsDirectory &&
                    csEntry.Size == exeEntry.Size &&
                    csEntry.StartSector == exeEntry.StartSector)
                {
                    matchCount++;
                }
                else
                {
                    mismatchCount++;
                    details.Add(
                        $"MISMATCH: {path} (C# size={csEntry.Size} sector={csEntry.StartSector} | exe size={exeEntry.Size} sector={exeEntry.StartSector})");
                }
            }
            else
            {
                mismatchCount++;
                details.Add($"ONLY IN C#: {path}");
            }
        }

        foreach (string path in exeByPath.Keys.Except(csByPath.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mismatchCount++;
            details.Add($"ONLY IN extract-xiso: {path}");
        }

        bool allMatch = mismatchCount == 0;
        if (allMatch)
        {
            details.Add($"{matchCount} entries match \u2713");
        }

        return new ListComparison(allMatch, string.Join("\n", details));
    }

    private sealed record DirComparison(bool AllMatch, string Detail);

    private static DirComparison CompareExtractedDirs(string csDir, string exeDir)
    {
        List<string> details = new();
        int mismatchCount = 0;
        int matchCount = 0;

        Dictionary<string, (string FullPath, string Relative)> csFiles = Directory
            .GetFiles(csDir, "*", SearchOption.AllDirectories)
            .Select(f => (FullPath: f, Relative: Path.GetRelativePath(csDir, f)))
            .ToDictionary(static x => x.Relative, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, (string FullPath, string Relative)> exeFiles = Directory
            .GetFiles(exeDir, "*", SearchOption.AllDirectories)
            .Select(f => (FullPath: f, Relative: Path.GetRelativePath(exeDir, f)))
            .ToDictionary(static x => x.Relative, StringComparer.OrdinalIgnoreCase);

        foreach ((string relative, (string FullPath, string Relative) csPath) in csFiles)
        {
            if (exeFiles.TryGetValue(relative, out (string FullPath, string Relative) exePath))
            {
                try
                {
                    string csHash = HashUtil.ComputeSha256(csPath.FullPath);
                    string exeHash = HashUtil.ComputeSha256(exePath.FullPath);
                    if (string.Equals(csHash, exeHash, StringComparison.Ordinal))
                    {
                        matchCount++;
                        if (matchCount <= 5)
                            details.Add($"\u2713 {relative}");
                    }
                    else
                    {
                        mismatchCount++;
                        details.Add($"\u2717 SHA-256 MISMATCH: {relative}\n  C#: {csHash}\n  exe: {exeHash}");
                    }
                }
                catch (Exception ex)
                {
                    mismatchCount++;
                    details.Add($"\u2717 Error hashing {relative}: {ex.Message}");
                }
            }
            else
            {
                mismatchCount++;
                details.Add($"ONLY IN C#: {relative}");
            }
        }

        foreach (string relative in exeFiles.Keys.Except(csFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mismatchCount++;
            details.Add($"ONLY IN extract-xiso: {relative}");
        }

        if (matchCount > 5)
        {
            details.Insert(5, $"... ({matchCount - 5} more matching files)");
        }

        bool allMatch = mismatchCount == 0;
        if (allMatch)
        {
            details.Clear();
            details.Add($"{matchCount} files extracted, all SHA-256 hashes match \u2713");
        }

        return new DirComparison(allMatch, string.Join("\n", details));
    }

    private static int CountFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CountFiles failed for {Dir}", dir);
            return 0;
        }
    }

    private static void DeleteDirectorySafe(string path, IProgress<TestProgress>? progress = null, string? file = null,
        int index = 0, int total = 0, string? test = null)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            // Best-effort delete, but never silent (TST-013): report the leaked size
            // so GB-scale temp leaks are visible in Serilog and the session log.
            long leakedBytes = GetDirectorySizeSafe(path);
            string sizeText = leakedBytes >= 0 ? FormatByteCount(leakedBytes) : "unknown size";
            Log.Warning(ex, "Temp cleanup failed for {Path} ({Size} left behind); manual cleanup may be needed", path,
                sizeText);
            if (progress is not null && !string.IsNullOrEmpty(file))
            {
                try
                {
                    progress.Report(new TestProgress(file, index, total, test ?? "Cleanup",
                        $"WARNING: Could not delete temp dir {path} ({sizeText} left behind). Manual cleanup may be needed."));
                }
                catch (Exception reportEx)
                {
                    Log.Warning(reportEx, "Cleanup warning report failed for {Path}", path);
                }
            }
        }
    }

    private static long GetDirectorySizeSafe(string path)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                               or NotSupportedException)
                {
                    // Unstatable file: skip it, keep the best-effort total.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return -1;
        }
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 0)
            return "unknown size";
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    /// <summary>
    /// Checks the temp-drive free space against <paramref name="requiredBytes"/> before
    /// large staging (TST-013). Returns a skip message when space is insufficient,
    /// or <c>null</c> when staging may proceed (including when free space is unknown).
    /// </summary>
    private static string? CheckTempSpace(long requiredBytes, string isoPath)
    {
        try
        {
            string? tempRoot = Path.GetPathRoot(Path.GetTempPath());
            if (string.IsNullOrEmpty(tempRoot))
                return null;

            DriveInfo drive = new(tempRoot);
            if (!drive.IsReady)
                return null;

            long free = drive.AvailableFreeSpace;
            if (free >= requiredBytes)
                return null;

            return $"Skipped: insufficient temp space on {drive.Name} for {Path.GetFileName(isoPath)} " +
                   $"(need ~{FormatByteCount(requiredBytes)}, have {FormatByteCount(free)} free). " +
                   "Free space or set TMP/TEMP to a larger drive.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(ex, "Temp space check failed; proceeding without a space gate");
            return null;
        }
    }

    private static long TryGetIsoLength(string isoPath)
    {
        try
        {
            return new FileInfo(isoPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return 0;
        }
    }

    private static string CreateTempSubDir(string name)
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "XISOSharpTester");

            // Full 128-bit GUID plus a retry loop on collision (TST-012): the old
            // 8-char prefix was only 32 bits and never retried, so parallel runs
            // could collide. Collisions are now astronomically unlikely, and a race
            // still retries instead of reusing a foreign directory.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string dir = Path.Combine(root, Guid.NewGuid().ToString("N"), name);
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                        return dir;
                    }

                    Log.Warning("Temp dir collision on attempt {Attempt}: {Dir}; retrying with a fresh GUID", attempt,
                        dir);
                }
                catch (IOException ex) when (attempt < 9)
                {
                    // Parallel-harness create race: retry with a fresh GUID.
                    Log.Warning(ex, "Temp dir create race on attempt {Attempt}: {Dir}; retrying", attempt, dir);
                }
            }

            string fallback = Path.Combine(root, Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CreateTempSubDir failed");
            BugReporter.ReportException(ex, "CreateTempSubDir failed");
            throw;
        }
    }

    private static void Report(IProgress<TestProgress>? progress, string file, int index, int total,
        string test, string status)
    {
        progress?.Report(new TestProgress(file, index, total, test, status));
        Log.Debug("[{Index}/{Total}] {File} - {Test}: {Status}", index, total, file, test, status);
    }
}
