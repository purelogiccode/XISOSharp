using System.Diagnostics;

namespace XISOSharp.BattleTests;

/// <summary>Battle-tester entry point: compares C# XISOSharp vs native extract-xiso.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("XISOSharp.BattleTests — C# vs extract-xiso (v2.7.1) battle tester");
        Console.WriteLine("================================================================");

        var exePath = FindExe(args);
        var dirs = ParseDirs(args);
        var explicitIsos = ParseIsoArgs(args);
        var createDirs = ParseCreateDirs(args);
        var help = args.Any(a => a is "-h" or "--help" or "/?" or "-?");

        if (help || args.Contains("--help-detailed"))
        {
            PrintUsage();
            return 0;
        }

        // Resolve ISO list
        var isoFiles = new List<string>();
        isoFiles.AddRange(explicitIsos.Where(File.Exists));

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[WARN] dir not found: {dir}");
                continue;
            }

            var found = Directory.GetFiles(dir, "*.iso", SearchOption.TopDirectoryOnly);
            Console.WriteLine($"Scan {dir}: {found.Length} *.iso (top-level)");
            isoFiles.AddRange(found);

            // Also include synthetic isos under _mp_work/mp2/isos if H:\ drives
            var mpIsos = Path.Combine(dir, "_mp_work", "mp2", "isos");
            if (Directory.Exists(mpIsos))
            {
                var mpFound = Directory.GetFiles(mpIsos, "*.iso", SearchOption.TopDirectoryOnly);
                Console.WriteLine($"Scan {mpIsos}: {mpFound.Length} *.iso");
                isoFiles.AddRange(mpFound);
            }

            // Also scan TestData if requested via H: not found fallback
            // For deep search, optionally add --recursive
            if (args.Contains("--recursive"))
            {
                var rec = Directory.GetFiles(dir, "*.iso", SearchOption.AllDirectories);
                // dedup already added
                foreach (var f in rec)
                {
                    if (!isoFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        isoFiles.Add(f);
                }

                Console.WriteLine($"Recursive total {rec.Length} iso");
            }
        }

        // Fallback to TestData/source if no H: isos found and no explicit isos
        if (isoFiles.Count == 0)
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData", "output",
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
                var testDataIso = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "output", "source.iso");
                if (File.Exists(testDataIso))
                {
                    isoFiles.Add(testDataIso);
                }
                else
                {
                    // Create a tiny synthetic ISO from a temp dir if nothing else
                    var tmpDir = Path.Combine(Path.GetTempPath(),
                        "battle_synth_src_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tmpDir);
                    File.WriteAllText(Path.Combine(tmpDir, "hello.txt"), "hello battle");
                    File.WriteAllText(Path.Combine(tmpDir, "data.bin"), new string('x', 4096));
                    var synthIso = Path.Combine(Path.GetTempPath(), $"battle_synth_{Guid.NewGuid():N}.iso");
                    try
                    {
                        var q = Logger.Quiet;
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
        if (isoFiles.Count == 0)
        {
            Console.WriteLine("No ISO files to test. Use --dirs H:\\XBOXTest or pass explicit .iso paths.");
            PrintUsage();
            return 1;
        }

        Console.WriteLine($"\nTesting {isoFiles.Count} ISO file(s):");
        foreach (var f in isoFiles.Take(10)) Console.WriteLine($"  - {f}");
        if (isoFiles.Count > 10) Console.WriteLine($"  ... + {isoFiles.Count - 10} more");
        Console.WriteLine($"Native exe: {exePath} {(File.Exists(exePath) ? "(found)" : "(NOT FOUND)")}");

        // Limit for performance if many files (H:\ has 37) — allow --all to force all, otherwise sample first 10 or use --limit
        var limit = ParseLimit(args);
        if (limit > 0 && isoFiles.Count > limit)
        {
            Console.WriteLine($"Limiting to first {limit} files (use --all or --limit N to change).");
            isoFiles = isoFiles.Take(limit).ToList();
        }

        var sw = Stopwatch.StartNew();
        var session = BattleRunner.RunAsync(isoFiles, exePath, createDirs).GetAwaiter().GetResult();
        sw.Stop();

        PrintSummary(session);
        WriteReports(session, isoFiles, exePath);

        return session.FailedSubTests > 0 || session.FailedFiles > 0 ? 2 : 0;
    }

    private static string FindExe(string[] args)
    {
        // --exe <path> takes precedence
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--native", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        // Check H:\ style default + project local
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "extract-xiso.exe"),
            @"C:\Users\HomePC\Dropbox\source\repos\CSharp_XISOSharp\XISOSharpTester\extract-xiso.exe",
            Path.Combine(Directory.GetCurrentDirectory(), "XISOSharpTester", "extract-xiso.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "extract-xiso.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return candidates[0];
    }

    private static string[] ParseDirs(string[] args)
    {
        var list = new List<string>();
        // --dirs H:\XBOXTest,H:\XBOX360Test or --dirs <a> --dirs <b>
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--dirs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var parts = args[i + 1].Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list.AddRange(parts);
            }
            else if (args[i].StartsWith("--dirs=", StringComparison.Ordinal))
            {
                var v = args[i].Substring("--dirs=".Length);
                var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list.AddRange(parts);
            }
        }

        if (list.Count == 0)
        {
            // Default H:\ drives if present, else no dirs (will use fallback)
            var defaults = new[] { @"H:\XBOXTest", @"H:\XBOX360Test" };
            foreach (var d in defaults)
            {
                if (Directory.Exists(d))
                    list.Add(d);
            }

            // Also include TestData dir if no H:
            if (list.Count == 0)
            {
                var td = Path.Combine(Directory.GetCurrentDirectory(), "TestData");
                if (Directory.Exists(td)) list.Add(td);
            }
        }

        return list.ToArray();
    }

    private static string[] ParseCreateDirs(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--create-dirs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], "--create", StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(args[i + 1].Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        // Also supports --create-dirs=<a,b>
        foreach (var a in args)
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
            var src = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "source");
            if (Directory.Exists(src))
            {
                list.Add(src);
            }
            else
            {
                var alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData",
                    "source"));
                if (Directory.Exists(alt)) list.Add(alt);
            }
        }

        return list.ToArray();
    }

    private static string[] ParseIsoArgs(string[] args)
    {
        // Any arg ending with .iso and not an option value for known options
        var isos = new List<string>();
        var skipNext = false;
        var optionValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--exe",
            "--native",
            "--dirs",
            "--create-dirs",
            "--create",
            "--limit"
        };
        for (var i = 0; i < args.Length; i++)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            var a = args[i];
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
            else if (a.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(a))
                isos.Add(a); // will be warned as not found later
        }

        return isos.ToArray();
    }

    private static int ParseLimit(string[] args)
    {
        if (args.Contains("--all")) return 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1],
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }

        foreach (var a in args)
        {
            if (a.StartsWith("--limit=", StringComparison.Ordinal) &&
                int.TryParse(a.AsSpan("--limit=".Length), System.Globalization.CultureInfo.InvariantCulture,
                    out var n))
            {
                return n;
            }
        }

        // Default limit 20 to keep battle fast on H:\ (37 files, each 7GB → too slow)
        return 5; // default to 5 ISOs + create battle; user can --limit 0 or --all for all
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""

                          Usage: XISOSharp.BattleTests [options] [*.iso ...]

                          Options:
                            --exe <path>              Path to extract-xiso.exe (default: XISOSharpTester/extract-xiso.exe)
                            --dirs <dir1,dir2>        Comma-separated dirs to scan for *.iso (default: H:\XBOXTest,H:\XBOX360Test if present)
                            --create-dirs <dir1,dir2> Dirs to test ISO creation parity (default: TestData/source)
                            --recursive               Scan dirs recursively for *.iso (top-level by default)
                            --limit <N>               Limit number of ISOs tested (default 5, use --all for all, --limit 0 for no limit)
                            --all                     Test all found ISOs (no limit)
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
    }

    private static void PrintSummary(BattleSessionResult s)
    {
        Console.WriteLine("\n================================================================");
        Console.WriteLine($"Battle Summary: {s.TotalFiles} item(s) in {s.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Native: {s.NativeVersion ?? "C# only"}");
        Console.WriteLine(
            $"  Files: {s.TotalFiles} total | {s.PassedFiles} passed | {s.FailedFiles} failed | {s.SkippedFiles} skipped");
        Console.WriteLine(
            $"  Checks: {s.TotalSubTests} total | {s.PassedSubTests} passed | {s.FailedSubTests} failed | {s.SkippedSubTests} skipped");
        var color = s.FailedSubTests > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        var prev = Console.ForegroundColor;
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
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "BattleReports");
            Directory.CreateDirectory(outDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var txtPath = Path.Combine(outDir, $"battle_{stamp}.txt");
            var jsonPath = Path.Combine(outDir, $"battle_{stamp}.json");

            using (var w = new StreamWriter(txtPath))
            {
                w.WriteLine($"XISOSharp Battle Report {stamp}");
                w.WriteLine($"Native: {exePath} | {s.NativeVersion}");
                w.WriteLine($"ISOs: {string.Join(", ", isoFiles)}");
                w.WriteLine(
                    $"Summary: {s.PassedSubTests}/{s.TotalSubTests} checks passed, {s.FailedSubTests} failed in {s.Elapsed.TotalSeconds:F1}s");
                w.WriteLine();
                foreach (var f in s.FileResults)
                {
                    w.WriteLine(
                        $"{f.FileName} ({f.FileSize} bytes) - {(f.HasFailures ? "FAIL" : "PASS")} {f.ElapsedSeconds:F1}s");
                    foreach (var sub in f.SubTests)
                        w.WriteLine($"  {sub.TestName,-14} {sub.Status,-7} {sub.Detail.Replace('\n', ' ').Trim()}");
                    w.WriteLine();
                }
            }

            Console.WriteLine($"Reports: {txtPath}");
            // JSON minimal
            var json = System.Text.Json.JsonSerializer.Serialize(
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