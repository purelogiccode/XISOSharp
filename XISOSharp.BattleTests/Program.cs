using System.Diagnostics;
using XISOSharp.BattleTests.Models;

namespace XISOSharp.BattleTests;

/// <summary>Battle-tester entry point: compares C# XISOSharp vs native extract-xiso.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("XISOSharp.BattleTests — C# vs extract-xiso (v2.7.1) battle tester");
        Console.WriteLine("================================================================");

        string exePath = FindExe(args);
        string[] dirs = ParseDirs(args);
        string[] explicitIsos = ParseIsoArgs(args);
        string[] createDirs = ParseCreateDirs(args);
        bool help = args.Any(a => a is "-h" or "--help" or "/?" or "-?");

        if (help || args.Contains("--help-detailed"))
        {
            PrintUsage();
            return 0;
        }

        // Resolve ISO list
        List<string> isoFiles = new();
        isoFiles.AddRange(explicitIsos.Where(File.Exists));

        // Missing explicit inputs (BUG-BTL-010): BattleRunner skips not-found files with
        // no FileResult, so track them here and emit failed FileResults after the run.
        List<string> missingExplicit = explicitIsos.Where(static f => !File.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[WARN] dir not found: {dir}");
                continue;
            }

            string[] found = Directory.GetFiles(dir, "*.iso", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"Scan {dir}: {found.Length} *.iso (top-level)");
            isoFiles.AddRange(found);

            // Also include synthetic isos under _mp_work/mp2/isos if H:\ drives
            string mpIsos = Path.Combine(dir, "_mp_work", "mp2", "isos");
            if (Directory.Exists(mpIsos))
            {
                string[] mpFound = Directory.GetFiles(mpIsos, "*.iso", SearchOption.TopDirectoryOnly);
                Console.WriteLine($"Scan {mpIsos}: {mpFound.Length} *.iso");
                isoFiles.AddRange(mpFound);
            }

            // Also scan TestData if requested via H: not found fallback
            // For deep search, optionally add --recursive
            if (args.Contains("--recursive"))
            {
                string[] rec = Directory.GetFiles(dir, "*.iso", SearchOption.AllDirectories);
                // dedup already added
                foreach (string f in rec)
                {
                    if (!isoFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        isoFiles.Add(f);
                }

                Console.WriteLine($"Recursive total {rec.Length} iso");
            }
        }

        // Fallback to TestData/source if no H: isos found and no explicit isos
        // (explicit missing inputs must not trigger a synthetic fallback that masks them).
        if (isoFiles.Count == 0 && explicitIsos.Length == 0)
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "output",
                "source.iso");
            fallback = Path.GetFullPath(fallback);
            if (File.Exists(fallback))
            {
                Console.WriteLine($"No ISOs found in dirs, using fallback {fallback}");
                isoFiles.Add(fallback);
            }
            else
            {
                // Also try relative TestData/source.iso generation on-the-fly?
                string testDataIso = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "output", "source.iso");
                if (File.Exists(testDataIso))
                {
                    isoFiles.Add(testDataIso);
                }
                else
                {
                    // Create a tiny synthetic ISO from a temp dir if nothing else
                    string tmpDir = Path.Combine(Path.GetTempPath(),
                        "battle_synth_src_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tmpDir);
                    File.WriteAllText(Path.Combine(tmpDir, "hello.txt"), "hello battle");
                    File.WriteAllText(Path.Combine(tmpDir, "data.bin"), new string('x', 4096));
                    string synthIso = Path.Combine(Path.GetTempPath(), $"battle_synth_{Guid.NewGuid():N}.iso");
                    try
                    {
                        bool q = Logger.Quiet;
                        Logger.Quiet = true;
                        try
                        {
                            XisoWriter.PackFromDirectory(tmpDir, synthIso);
                        }
                        finally
                        {
                            Logger.Quiet = q;
                        }

                        Console.WriteLine($"No ISOs found → created synthetic {synthIso}");
                        isoFiles.Add(synthIso);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to create synthetic ISO: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            Directory.Delete(tmpDir, true);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
            }
        }

        isoFiles = isoFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (isoFiles.Count == 0 && missingExplicit.Count == 0)
        {
            Console.WriteLine("No ISO files to test. Use --dirs H:\\XBOXTest or pass explicit .iso paths.");
            PrintUsage();
            return 1;
        }

        Console.WriteLine($"\nTesting {isoFiles.Count} ISO file(s):");
        foreach (string f in isoFiles.Take(10)) Console.WriteLine($"  - {f}");
        if (isoFiles.Count > 10) Console.WriteLine($"  ... + {isoFiles.Count - 10} more");
        foreach (string m in missingExplicit) Console.WriteLine($"  - {m} (NOT FOUND)");
        Console.WriteLine($"Native exe: {exePath} {(File.Exists(exePath) ? "(found)" : "(NOT FOUND)")}");

        // Limit for performance if many files (H:\ has 37) — allow --all to force all, otherwise sample first 10 or use --limit
        int limit = ParseLimit(args);
        if (limit > 0 && isoFiles.Count > limit)
        {
            Console.WriteLine($"Limiting to first {limit} files (use --all or --limit N to change).");
            isoFiles = isoFiles.Take(limit).ToList();
        }

        Stopwatch sw = Stopwatch.StartNew();
        bool onlyExtended = args.Any(a => string.Equals(a, "--extended-only", StringComparison.OrdinalIgnoreCase));
        BattleSessionResult session = onlyExtended
            ? new BattleSessionResult()
            : BattleRunner.RunAsync(isoFiles, exePath, createDirs).GetAwaiter().GetResult();

        if (args.Any(a => string.Equals(a, "--extended", StringComparison.OrdinalIgnoreCase)) || onlyExtended)
        {
            string xkPath = FindOracle(args, "--xboxkit", "xboxkit.exe");
            string xdPath = FindOracle(args, "--xdvdfs", "xdvdfs.exe");
            bool keepSandbox = args.Contains("--keep-sandbox", StringComparer.OrdinalIgnoreCase);
            using XboxKitWrapper xk = new(xkPath);
            using XdvdfsWrapper xd = new(xdPath);
            Console.WriteLine($"\nExtended battle — xboxkit: {xkPath} {(xk.Available ? "(found)" : "(NOT FOUND)")}");
            Console.WriteLine($"Extended battle — xdvdfs: {xdPath} {(xd.Available ? "(found)" : "(NOT FOUND)")}");
            if (xk.Available)
                Console.WriteLine($"  xboxkit: {xk.GetVersion()}");
            if (xd.Available)
                Console.WriteLine($"  xdvdfs: {xd.GetVersion()}");
            for (int i = 0; i < isoFiles.Count; i++)
            {
                string file = isoFiles[i];
                if (!File.Exists(file))
                    continue;
                Console.Write($"[EXT {i + 1}/{isoFiles.Count}] {Path.GetFileName(file)} ... ");
                PerFileBattleResult er = ExtendedBattleRunner.RunExtendedForIso(file, xk, xd, keepSandbox);
                session.FileResults.Add(er);
                ConsoleColor color = er.HasFailures ? ConsoleColor.Red : ConsoleColor.Green;
                ConsoleColor prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(
                    $"{(er.HasFailures ? "FAIL" : "PASS")} ({er.ElapsedSeconds:F1}s) {string.Join(" ", er.SubTests.Select(s => $"{s.TestName}:{Symbol(s.Status)}"))}");
                Console.ForegroundColor = prev;
                foreach (SubBattleResult sub in er.SubTests.Where(s => s.Status is BattleStatus.Failed or BattleStatus.Error))
                    Console.WriteLine($"  \u2717 {sub.TestName}: {sub.Detail.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }

        sw.Stop();

        AddMissingFileResults(session, missingExplicit);

        PrintSummary(session);
        WriteReports(session, isoFiles.Concat(missingExplicit).ToList(), exePath);

        return session.FailedSubTests > 0 || session.FailedFiles > 0 || session.ErrorSubTests > 0 ? 2 : 0;
    }

    private static void AddMissingFileResults(BattleSessionResult session, IReadOnlyList<string> missing)
    {
        // Phantom ISOs (BUG-BTL-010) must fail the run instead of silent skip:
        // BattleRunner skips not-found files with no FileResult, so synthesize one here.
        foreach (string path in missing)
        {
            Console.WriteLine($"[MISSING] {path} ... FAIL (not found)");
            string fileName;
            try
            {
                fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = path;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                fileName = path;
            }

            PerFileBattleResult result = new()
            {
                FilePath = path,
                FileName = fileName,
                FileSize = 0,
                ElapsedSeconds = 0,
            };
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "InputExists",
                Status = BattleStatus.Failed,
                Detail = $"File not found: {path}",
                ElapsedSeconds = 0,
            });
            session.FileResults.Add(result);
        }
    }

    private static string FindExe(string[] args)
    {
        // Explicit override wins even when missing (surfaces the typo as NOT FOUND downstream).
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--native", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        // Shared chain (BUG-BTL-002/BUG-X-005, mirrors Gui CliLocator coverage):
        // sibling of the harness (OS-aware) then PATH. The -v probe runs later via
        // BattleRunner (wrapper.GetVersion), same as Gui Resolve+Probe split.
        string? resolved = ToolLocator.Resolve(null, "extract-xiso.exe", "extract-xiso");
        if (resolved is not null)
        {
            return resolved;
        }

        string fallbackName = OperatingSystem.IsWindows() ? "extract-xiso.exe" : "extract-xiso";
        return Path.Combine(AppContext.BaseDirectory, fallbackName);
    }

    private static string[] ParseDirs(string[] args)
    {
        List<string> list = new();
        // --dirs H:\XBOXTest,H:\XBOX360Test or --dirs <a> --dirs <b>
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--dirs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string[] parts = args[i + 1].Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list.AddRange(parts);
            }
            else if (args[i].StartsWith("--dirs=", StringComparison.Ordinal))
            {
                string v = args[i].Substring("--dirs=".Length);
                string[] parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list.AddRange(parts);
            }
        }

        if (list.Count == 0)
        {
            // Default H:\ drives if present, else no dirs (will use fallback)
            string[] defaults = new[] { @"H:\XBOXTest", @"H:\XBOX360Test" };
            foreach (string d in defaults)
            {
                if (Directory.Exists(d))
                    list.Add(d);
            }

            // Also include TestData dir if no H:
            if (list.Count == 0)
            {
                string td = Path.Combine(Directory.GetCurrentDirectory(), "TestData");
                if (Directory.Exists(td)) list.Add(td);
            }
        }

        return list.ToArray();
    }

    private static string[] ParseCreateDirs(string[] args)
    {
        List<string> list = new();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--create-dirs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--create", StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(args[i + 1].Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        // Also supports --create-dirs=<a,b>
        foreach (string a in args)
        {
            if (a.StartsWith("--create-dirs=", StringComparison.Ordinal))
            {
                list.AddRange(a.Substring("--create-dirs=".Length).Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        // If not specified but we have a temp synth dir, we can test create via H:\XBOXTest extracted trees?
        // For now, also test create from TestData/source if no explicit create dirs
        if (list.Count == 0)
        {
            string src = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "source");
            if (Directory.Exists(src))
            {
                list.Add(src);
            }
            else
            {
                string alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData",
                    "source"));
                if (Directory.Exists(alt)) list.Add(alt);
            }
        }

        return list.ToArray();
    }

    private static string[] ParseIsoArgs(string[] args)
    {
        // Any arg ending with .iso and not an option value for known options
        List<string> isos = new();
        bool skipNext = false;
        HashSet<string> optionValues = new(StringComparer.Ordinal)
        {
            "--exe",
            "--native",
            "--dirs",
            "--create-dirs",
            "--create",
            "--limit"
        };
        for (int i = 0; i < args.Length; i++)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            string a = args[i];
            if (optionValues.Contains(a))
            {
                skipNext = true;
                continue;
            }

            if (a.StartsWith("--dirs=", StringComparison.Ordinal) ||
                a.StartsWith("--create-dirs=", StringComparison.Ordinal) ||
                a.StartsWith("--limit=", StringComparison.Ordinal))
            {
                continue;
            }

            if (a is "--recursive" or "--all" or "-h" or "--help") continue;
            if (a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                isos.Add(Path.GetFullPath(a));
            else if (a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                isos.Add(a); // missing input (rooted or relative): reported as a failed FileResult later (BUG-BTL-010)
        }

        return isos.ToArray();
    }

    private static int ParseLimit(string[] args)
    {
        if (args.Contains("--all")) return 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1],
                    System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                return n;
            }
        }

        foreach (string a in args)
        {
            if (a.StartsWith("--limit=", StringComparison.Ordinal) &&
                int.TryParse(a.AsSpan("--limit=".Length), System.Globalization.CultureInfo.InvariantCulture,
                    out int n))
            {
                return n;
            }
        }

        // Default limit 20 to keep battle fast on H:\ (37 files, each 7GB → too slow)
        return 5; // default to 5 ISOs + create battle; user can --limit 0 or --all for all
    }

    private static string FindOracle(string[] args, string flag, string fileName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        string here = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(here))
            return here;
        string local = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (File.Exists(local))
            return local;
        return here;
    }

    private static string Symbol(BattleStatus s) =>
        s switch
        {
            BattleStatus.Passed => "\u2713", BattleStatus.Failed => "\u2717", BattleStatus.Skipped => "-", _ => "?"
        };

    private static void PrintUsage() =>
        Console.WriteLine("""

                          Usage: XISOSharp.BattleTests [options] [*.iso ...]

                          Options:
                            --exe <path>              Path to extract-xiso.exe (default: XISOSharpTester/extract-xiso.exe)
                            --dirs <dir1,dir2>        Comma-separated dirs to scan for *.iso (default: H:\XBOXTest,H:\XBOX360Test if present)
                            --create-dirs <dir1,dir2> Dirs to test ISO creation parity (default: TestData/source)
                            --recursive               Scan dirs recursively for *.iso (top-level by default)
                            --limit <N>               Limit number of ISOs tested (default 5, use --all for all, --limit 0 for no limit)
                            --all                     Test all found ISOs (no limit)
                            --extended                Extended battles: XboxKit-borrowed features vs xboxkit.exe,
                                                      xdvdfs-borrowed features vs xdvdfs.exe (oracles beside the harness)
                            --extended-only           Extended battles without the extract-xiso parity battle
                            --keep-sandbox            Keep extended-battle sandboxes for debugging (default: delete; they hold ~4x the ISO size)
                            --xboxkit <path>          Path to xboxkit.exe (default: beside the harness)
                            --xdvdfs <path>           Path to xdvdfs.exe (default: beside the harness)
                            -h, --help                Show this help

                          Examples:
                            XISOSharp.BattleTests --exe C:\path\extract-xiso.exe
                            XISOSharp.BattleTests --dirs H:\XBOXTest --limit 10
                            XISOSharp.BattleTests D:\my.iso E:\other.iso --create-dirs C:\myGameFolder
                            XISOSharp.BattleTests --dirs H:\XBOXTest,H:\XBOX360Test --recursive --all

                          Comparisons per ISO:
                            Verify, Audit, List, Extract (SHA256), Rewrite (SHA256), CISO round-trip, Checksum, BlockDevice
                          Plus:
                            Create parity (dir -> iso via both tools), Directory Listing compare, Extract SHA256 compare
                            Advanced self-tests: Remap, WaxGlob, Ranges, XgdTables, SecuritySectors, Prng, Ops, GlobMatcher

                          """);

    private static void PrintSummary(BattleSessionResult s)
    {
        Console.WriteLine("\n================================================================");
        Console.WriteLine($"Battle Summary: {s.TotalFiles} item(s) in {s.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Native: {s.NativeVersion ?? "C# only"}");
        Console.WriteLine(
            $"  Files: {s.TotalFiles} total | {s.PassedFiles} passed | {s.FailedFiles} failed | {s.SkippedFiles} skipped");
        Console.WriteLine(
            $"  Checks: {s.TotalSubTests} total | {s.PassedSubTests} passed | {s.FailedSubTests} failed | {s.SkippedSubTests} skipped");
        ConsoleColor color = s.FailedSubTests > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        ConsoleColor prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(s.FailedSubTests == 0
            ? "  RESULT: ALL CHECKS PASSED ✓"
            : $"  RESULT: {s.FailedSubTests} CHECK(S) FAILED ✗");
        Console.ForegroundColor = prev;
        Console.WriteLine("================================================================");
    }

    private static void WriteReports(BattleSessionResult s, IList<string> isoFiles, string exePath)
    {
        try
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "BattleReports");
            Directory.CreateDirectory(outDir);
            // BTL-022: sub-second + PID component so concurrent runs never overwrite
            // each other's reports (second-granularity stamps collide).
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture)
                           + "_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string txtPath = Path.Combine(outDir, $"battle_{stamp}.txt");
            string jsonPath = Path.Combine(outDir, $"battle_{stamp}.json");

            using (StreamWriter w = new(txtPath))
            {
                w.WriteLine($"XISOSharp Battle Report {stamp}");
                w.WriteLine($"Native: {exePath} | {s.NativeVersion}");
                w.WriteLine($"ISOs: {string.Join(", ", isoFiles)}");
                w.WriteLine(
                    $"Summary: {s.PassedSubTests}/{s.TotalSubTests} checks passed, {s.FailedSubTests} failed in {s.Elapsed.TotalSeconds:F1}s");
                w.WriteLine();
                foreach (PerFileBattleResult f in s.FileResults)
                {
                    w.WriteLine(
                        $"{f.FileName} ({f.FileSize} bytes) - {(f.HasFailures ? "FAIL" : "PASS")} {f.ElapsedSeconds:F1}s");
                    foreach (SubBattleResult sub in f.SubTests)
                        w.WriteLine($"  {sub.TestName,-14} {sub.Status,-7} {sub.Detail.Replace('\n', ' ').Trim()}");
                    w.WriteLine();
                }
            }

            Console.WriteLine($"Reports: {txtPath}");
            // JSON minimal
            string json = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    timestamp = stamp,
                    native = s.NativeVersion,
                    exePath,
                    totalFiles = s.TotalFiles,
                    passedFiles = s.PassedFiles,
                    failedFiles = s.FailedFiles,
                    totalChecks = s.TotalSubTests,
                    passedChecks = s.PassedSubTests,
                    failedChecks = s.FailedSubTests,
                    elapsedSeconds = s.Elapsed.TotalSeconds,
                    files = s.FileResults.Select(f => new
                    {
                        f.FileName,
                        f.FilePath,
                        f.FileSize,
                        f.ElapsedSeconds,
                        allPassed = f.AllPassed,
                        subTests = f.SubTests.Select(st =>
                            new { st.TestName, status = st.Status.ToString(), st.Detail, st.ElapsedSeconds })
                    })
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"         {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to write reports: {ex.Message}");
        }
    }
}
