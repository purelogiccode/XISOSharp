using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using XISOSharp;
using XISOSharpTester.Models;

namespace XISOSharpTester.Services;

public class XisoTestRunner
{
    private static string? _xisoSharpVersion;

    public string? XisoSharpVersion => _xisoSharpVersion;

    public async Task<TestSessionResult> RunAsync(
        List<XisoFileEntry> files,
        string xisoSharpExePath,
        IProgress<TestProgress>? progress = null)
    {
        var session = new TestSessionResult();
        var exeAvailable = File.Exists(xisoSharpExePath);

        using var wrapper = exeAvailable ? new XisoSharpWrapper(xisoSharpExePath) : null;

        if (wrapper != null)
        {
            _xisoSharpVersion = wrapper.GetVersion();
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            progress?.Report(new TestProgress(file.FileName, i + 1, files.Count,
                "Starting", $"Testing {file.FileName}..."));

            var result = await Task.Run(() => TestSingleFile(file, wrapper, progress, i, files.Count));
            session.FileResults.Add(result);
        }

        progress?.Report(new TestProgress("Done", files.Count, files.Count,
            "Complete", "All tests finished.", true));

        return session;
    }

    private static PerFileResult TestSingleFile(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles)
    {
        var sw = Stopwatch.StartNew();
        var result = new PerFileResult
        {
            FileName = entry.FileName,
            FilePath = entry.FilePath,
            FileSize = entry.FileSize
        };

        var path = entry.FilePath;

        if (!File.Exists(path))
        {
            result.SubTests.Add(new SubTestResult
            {
                TestName = "All Tests",
                Status = TestStatus.Skipped,
                Detail = "File not found on disk."
            });
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        // Test 1: Verify XISO header
        RunVerifyTest(entry, wrapper, progress, fileIndex, totalFiles, result);

        // Test 2: List files comparison
        RunListTest(entry, wrapper, progress, fileIndex, totalFiles, result);

        // Test 3: Extract all files & hash comparison
        RunExtractTest(entry, wrapper, progress, fileIndex, totalFiles, result);

        // Test 4: Rewrite comparison
        RunRewriteTest(entry, wrapper, progress, fileIndex, totalFiles, result);

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        Log.Information("[{Status}] {File} ({Time:N1}s)",
            result.AllPassed ? "PASS" : "FAIL", entry.FileName, result.ElapsedSeconds);

        return result;
    }

    private static void RunVerifyTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Verify",
            "Validating XISO header...");

        var tSw = Stopwatch.StartNew();
        try
        {
            using var fs = File.OpenRead(entry.FilePath);
            var (rootDirSector, rootDirSize, discLseek) = XisoReader.VerifyXiso(fs, entry.FileName);
            tSw.Stop();

            var csDetail = $"Valid XISO | RootSector={rootDirSector} RootSize={rootDirSize} DiscLseek={discLseek}";

            if (wrapper is { Available: true })
            {
                var exeResult = wrapper.ListFiles(entry.FilePath);
                var exeValid = exeResult.ExitCode == 0;
                var exeDetail = exeValid ? "extract-xiso: valid" : $"extract-xiso: exit code {exeResult.ExitCode}";

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
        catch (Exception ex)
        {
            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Verify XISO",
                Status = TestStatus.Failed,
                Detail = $"C# error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static void RunListTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "List",
            "Comparing file listing...");

        var tSw = Stopwatch.StartNew();
        try
        {
            // Get C# listing
            var csOutput = CaptureCSharpListOutput(entry.FilePath);
            var csEntries = ParseListOutput(csOutput);

            if (wrapper is { Available: true })
            {
                var exeResult = wrapper.ListFiles(entry.FilePath);
                if (exeResult.ExitCode == 0)
                {
                    var exeEntries = ParseListOutput(exeResult.StdOut);
                    var comparison = CompareListEntries(csEntries, exeEntries);
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
        catch (Exception ex)
        {
            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "List Files",
                Status = TestStatus.Failed,
                Detail = $"Error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static void RunExtractTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Extract",
            "Extracting and comparing file hashes...");

        var tSw = Stopwatch.StartNew();
        try
        {
            var csTempDir = CreateTempSubDir("cs_extract");
            var exeTempDir = CreateTempSubDir("exe_extract");
            try
            {
                // C# extraction
                var originalDir = Environment.CurrentDirectory;
                try
                {
                    // Logger.Quiet = true needed to suppress output
                    var saveQuiet = Logger.Quiet;
                    Logger.Quiet = true;
                    try
                    {
                        XisoReader.DecodeXiso(entry.FilePath, csTempDir,
                            ExtractMode.Extract, out _, false);
                    }
                    finally
                    {
                        Logger.Quiet = saveQuiet;
                    }
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
                    var exeResult = wrapper.ExtractFiles(entry.FilePath, exeTempDir);
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

                    var comparison = CompareExtractedDirs(csTempDir, exeTempDir);
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
                    var csFileCount = CountFiles(csTempDir);
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
                DeleteDirectorySafe(csTempDir);
                DeleteDirectorySafe(exeTempDir);
            }
        }
        catch (Exception ex)
        {
            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Extract & Hash Compare",
                Status = TestStatus.Failed,
                Detail = $"Error: {ex.Message}",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
    }

    private static void RunRewriteTest(
        XisoFileEntry entry,
        XisoSharpWrapper? wrapper,
        IProgress<TestProgress>? progress,
        int fileIndex,
        int totalFiles,
        PerFileResult result)
    {
        Report(progress, entry.FileName, fileIndex + 1, totalFiles, "Rewrite",
            "Rewriting and comparing ISO hashes...");

        if (wrapper is not { Available: true })
        {
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Rewrite Compare",
                Status = TestStatus.Skipped,
                Detail = "extract-xiso.exe not available."
            });
            return;
        }

        var tSw = Stopwatch.StartNew();
        var csWorkDir = CreateTempSubDir("cs_rewrite");
        var exeWorkDir = CreateTempSubDir("exe_rewrite");
        try
        {
            // C# rewrite
            var csInput = Path.Combine(csWorkDir, entry.FileName);
            File.Copy(entry.FilePath, csInput, true);

            // Check if already optimized (tag at offset 31337)
            using (var fs = File.OpenRead(csInput))
            {
                if (fs.Length > Constants.OptimizedTagOffset + Constants.OptimizedTag.Length)
                {
                    fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                    Span<byte> tagBuf = stackalloc byte[Constants.OptimizedTag.Length];
                    fs.ReadExactly(tagBuf);
                    var tag = Encoding.ASCII.GetString(tagBuf);
                    if (tag == Constants.OptimizedTag)
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

            var csOutDir = Path.Combine(csWorkDir, "cs_out");
            Directory.CreateDirectory(csOutDir);

            var saveQuiet = Logger.Quiet;
            Logger.Quiet = true;
            try
            {
                XisoReader.DecodeXiso(csInput, csOutDir, ExtractMode.Rewrite, out var csOutPath, false);
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

            // Find the C# output ISO
            // Maybe in csWorkDir
            var csIsoOutput = Directory.GetFiles(csOutDir, "*.iso").FirstOrDefault() ?? Directory.GetFiles(csWorkDir, "*.iso", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase));

            // extract-xiso rewrite
            var exeInput = Path.Combine(exeWorkDir, entry.FileName);
            File.Copy(entry.FilePath, exeInput, true);

            var exeOutDir = Path.Combine(exeWorkDir, "exe_out");
            Directory.CreateDirectory(exeOutDir);

            var exeResult = wrapper.Rewrite(exeInput, exeOutDir);
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

            var exeIsoOutput = Directory.GetFiles(exeOutDir, "*.iso").FirstOrDefault();

            if (csIsoOutput == null || exeIsoOutput == null)
            {
                tSw.Stop();
                result.SubTests.Add(new SubTestResult
                {
                    TestName = "Rewrite Compare",
                    Status = TestStatus.Failed,
                    Detail = $"Could not locate output ISOs. C#: {(csIsoOutput ?? "null")} | exe: {(exeIsoOutput ?? "null")}",
                    ElapsedSeconds = tSw.Elapsed.TotalSeconds
                });
                return;
            }

            var csHash = HashUtil.ComputeSha256(csIsoOutput);
            var exeHash = HashUtil.ComputeSha256(exeIsoOutput);
            var match = csHash == exeHash;
            var csSize = new FileInfo(csIsoOutput).Length;
            var exeSize = new FileInfo(exeIsoOutput).Length;

            tSw.Stop();
            result.SubTests.Add(new SubTestResult
            {
                TestName = "Rewrite Compare",
                Status = match ? TestStatus.Passed : TestStatus.Failed,
                Detail = match
                    ? $"SHA-256: {csHash} ✓ ({(csSize / (1024.0 * 1024)):F1} MB vs {(exeSize / (1024.0 * 1024)):F1} MB)"
                    : $"SHA-256 MISMATCH\nC#:      {csHash} ({(csSize / (1024.0 * 1024)):F1} MB)\nextract-xiso: {exeHash} ({(exeSize / (1024.0 * 1024)):F1} MB)",
                ElapsedSeconds = tSw.Elapsed.TotalSeconds
            });
        }
        catch (Exception ex)
        {
            tSw.Stop();
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
            DeleteDirectorySafe(csWorkDir);
            DeleteDirectorySafe(exeWorkDir);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string CaptureCSharpListOutput(string isoPath)
    {
        var saveQuiet = Logger.Quiet;
        var originalOut = Console.Out;
        try
        {
            Logger.Quiet = false;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            XisoReader.DecodeXiso(isoPath, null, ExtractMode.List, out _, false);
            sw.Flush();
            return sw.ToString();
        }
        catch (ExtractErrorException)
        {
            return string.Empty;
        }
        finally
        {
            Logger.Quiet = saveQuiet;
            Console.SetOut(originalOut);
        }
    }

    private sealed record ListEntry(
        string Path,
        bool IsDirectory,
        uint Size,
        uint StartSector);

    private static List<ListEntry> ParseListOutput(string output)
    {
        var entries = new List<ListEntry>();

        // Matches: " - Path: filename                        Size: N bytes,  StartSector: S"
        // or with nesting: " - filename                        Size: N bytes,  StartSector: S"
        // or dir: " - dirname                                 DIR"
        var fileRegex = new Regex(@"^\s*-\s+(.*?)\s{2,}Size:\s*(\d+)\s*bytes,\s*StartSector:\s*(\d+)", RegexOptions.Multiline);
        var dirRegex = new Regex(@"^\s*-\s+(.*?)\s{2,}DIR", RegexOptions.Multiline);

        foreach (Match m in fileRegex.Matches(output))
        {
            var path = m.Groups[1].Value.Trim();
            var size = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var sector = uint.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            entries.Add(new ListEntry(path, false, size, sector));
        }

        foreach (Match m in dirRegex.Matches(output))
        {
            var path = m.Groups[1].Value.Trim();
            entries.Add(new ListEntry(path, true, 0, 0));
        }

        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    private sealed record ListComparison(bool AllMatch, string Detail);

    private static ListComparison CompareListEntries(List<ListEntry> csEntries, List<ListEntry> exeEntries)
    {
        var details = new List<string>();

        if (csEntries.Count != exeEntries.Count)
        {
            details.Add($"File count: C#={csEntries.Count} extract-xiso={exeEntries.Count}");
        }

        var csByPath = csEntries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var exeByPath = exeEntries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        var matchCount = 0;
        var mismatchCount = 0;

        foreach (var (path, csEntry) in csByPath)
        {
            if (exeByPath.TryGetValue(path, out var exeEntry))
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
                    details.Add($"MISMATCH: {path} (C# size={csEntry.Size} sector={csEntry.StartSector} | exe size={exeEntry.Size} sector={exeEntry.StartSector})");
                }
            }
            else
            {
                mismatchCount++;
                details.Add($"ONLY IN C#: {path}");
            }
        }

        foreach (var path in exeByPath.Keys.Except(csByPath.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mismatchCount++;
            details.Add($"ONLY IN extract-xiso: {path}");
        }

        var allMatch = mismatchCount == 0;
        if (allMatch)
        {
            details.Add($"{matchCount} entries match ✓");
        }

        return new ListComparison(allMatch, string.Join("\n", details));
    }

    private sealed record DirComparison(bool AllMatch, string Detail);

    private static DirComparison CompareExtractedDirs(string csDir, string exeDir)
    {
        var details = new List<string>();
        var mismatchCount = 0;
        var matchCount = 0;

        var csFiles = Directory.GetFiles(csDir, "*", SearchOption.AllDirectories)
            .Select(f => (FullPath: f, Relative: Path.GetRelativePath(csDir, f)))
            .ToDictionary(x => x.Relative, StringComparer.OrdinalIgnoreCase);

        var exeFiles = Directory.GetFiles(exeDir, "*", SearchOption.AllDirectories)
            .Select(f => (FullPath: f, Relative: Path.GetRelativePath(exeDir, f)))
            .ToDictionary(x => x.Relative, StringComparer.OrdinalIgnoreCase);

        foreach (var (relative, csPath) in csFiles)
        {
            if (exeFiles.TryGetValue(relative, out var exePath))
            {
                try
                {
                    var csHash = HashUtil.ComputeSha256(csPath.FullPath);
                    var exeHash = HashUtil.ComputeSha256(exePath.FullPath);
                    if (csHash == exeHash)
                    {
                        matchCount++;
                        if (matchCount <= 5)
                            details.Add($"✓ {relative}");
                    }
                    else
                    {
                        mismatchCount++;
                        details.Add($"✗ SHA-256 MISMATCH: {relative}\n  C#: {csHash}\n  exe: {exeHash}");
                    }
                }
                catch (Exception ex)
                {
                    mismatchCount++;
                    details.Add($"✗ Error hashing {relative}: {ex.Message}");
                }
            }
            else
            {
                mismatchCount++;
                details.Add($"ONLY IN C#: {relative}");
            }
        }

        foreach (var relative in exeFiles.Keys.Except(csFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mismatchCount++;
            details.Add($"ONLY IN extract-xiso: {relative}");
        }

        if (matchCount > 5)
        {
            details.Insert(5, $"... ({matchCount - 5} more matching files)");
        }

        var allMatch = mismatchCount == 0;
        if (allMatch)
        {
            details.Clear();
            details.Add($"{matchCount} files extracted, all SHA-256 hashes match ✓");
        }

        return new DirComparison(allMatch, string.Join("\n", details));
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
        catch { return 0; }
    }

    private static void DeleteDirectorySafe(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* best effort */ }
    }

    private static string CreateTempSubDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "XISOSharpTester",
            Guid.NewGuid().ToString("N").Substring(0, 8), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Report(IProgress<TestProgress>? progress, string file, int index, int total,
        string test, string status)
    {
        progress?.Report(new TestProgress(file, index, total, test, status));
        Log.Debug("[{Index}/{Total}] {File} - {Test}: {Status}", index, total, file, test, status);
    }
}
