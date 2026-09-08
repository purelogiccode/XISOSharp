using System.Diagnostics;
using System.Globalization;

namespace XISOSharp.BattleTests;

/// <summary>
/// Battle-tester entry point: drives the XISOSharp CLI against native
/// extract-xiso.exe over a random sample of ISOs (default: 3 from H:\XBOXTest).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("XISOSharp.BattleTests — XISOSharp CLI vs extract-xiso (v2.7.1) CLI battle");
        Console.WriteLine("=========================================================================");

        BattleOptions opt;
        try
        {
            opt = BattleOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            BattleOptions.PrintUsage();
            return 1;
        }

        if (opt.Help)
        {
            BattleOptions.PrintUsage();
            return 0;
        }

        ToolProcess cli = new(ResolveCli(opt.CliPath)) { TimeoutMs = opt.TimeoutMinutes * 60_000 };
        ToolProcess oracle = new(ResolveOracle(opt.OraclePath)) { TimeoutMs = opt.TimeoutMinutes * 60_000 };
        ToolProcess? xdvdfs = ResolveOptional(opt.XdvdfsPath, "xdvdfs.exe", opt.TimeoutMinutes * 60_000);
        ToolProcess? xboxkit = ResolveOptional(opt.XboxkitPath, "xboxkit.exe", opt.TimeoutMinutes * 60_000);
        Console.WriteLine($"CLI exe:    {cli.ExePath} {(cli.Available ? "(found)" : "(NOT FOUND)")}");
        Console.WriteLine($"Oracle exe: {oracle.ExePath} {(oracle.Available ? "(found)" : "(NOT FOUND)")}");
        Console.WriteLine($"xdvdfs exe: {(xdvdfs is null ? "(not provided)" : xdvdfs.ExePath)} {(xdvdfs?.Available == true ? "(found)" : "(MISSING — xdvdfs ops skipped)")}");
        Console.WriteLine($"xboxkit:    {(xboxkit is null ? "(not provided)" : xboxkit.ExePath)} {(xboxkit?.Available == true ? "(found)" : "(MISSING — xboxkit ops skipped)")}");
        if (!cli.Available || !oracle.Available)
        {
            Console.WriteLine("[ERROR] Both executables are required for a CLI-vs-CLI battle.");
            return 1;
        }

        // Sample: explicit positional ISOs win; otherwise N random distinct ISOs
        // from the scanned dirs (seed reported for reproducibility).
        int seed = opt.Seed ?? Random.Shared.Next();
        List<string> pool = CollectIsos(opt.Dirs);
        List<string> picked = opt.ExplicitIsos.Count > 0 ? opt.ExplicitIsos : PickRandom(pool, opt.Count, seed);
        if (picked.Count == 0)
        {
            Console.WriteLine("[ERROR] No ISO files found. Use --dir <path> or pass explicit *.iso paths.");
            return 1;
        }

        Console.WriteLine($"\nSeed: {seed} | Sample: {picked.Count} of {pool.Count} ISO(s) | Ops: {string.Join(", ", opt.Ops)}");
        foreach (string f in picked)
        {
            Console.WriteLine($"  - {f}");
        }

        string workRoot = opt.WorkRoot ?? Path.Combine(
            Path.GetTempPath(),
            "xiso_battle_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" +
            Environment.ProcessId);
        Directory.CreateDirectory(workRoot);
        Console.WriteLine($"Work root: {workRoot}");

        BattleSession session = new()
        {
            Seed = seed,
            CliPath = cli.ExePath,
            OraclePath = oracle.ExePath,
            CliVersion = cli.GetVersion(),
            OracleVersion = oracle.GetVersion(),
            XdvdfsPath = xdvdfs?.Available == true ? xdvdfs.ExePath : string.Empty,
            XdvdfsVersion = xdvdfs?.Available == true ? xdvdfs.GetVersion() : "not found",
            XboxkitPath = xboxkit?.Available == true ? xboxkit.ExePath : string.Empty,
            XboxkitVersion = xboxkit?.Available == true ? xboxkit.GetVersion() : "not found",
            WorkRoot = workRoot,
            Ops = opt.Ops,
        };
        Console.WriteLine($"CLI:       {session.CliVersion}");
        Console.WriteLine($"Oracle:    {session.OracleVersion}");
        Console.WriteLine($"xdvdfs:    {session.XdvdfsVersion}");
        Console.WriteLine($"xboxkit:   {session.XboxkitVersion}\n");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < picked.Count; i++)
        {
            IsoResult r = BattleRunner.RunIso(picked[i], opt, cli, oracle, xdvdfs, xboxkit, workRoot, i + 1, picked.Count);
            session.IsoResults.Add(r);
            Console.WriteLine();
        }

        sw.Stop();
        session.Elapsed = sw.Elapsed;

        PrintSummary(session);
        BattleReport.Write(session, picked);
        CleanupWorkRoot(workRoot, opt.KeepWork);

        return session.FailedSubs > 0 ? 2 : 0;
    }

    private static List<string> CollectIsos(IEnumerable<string> dirs)
    {
        List<string> pool = [];
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[WARN] dir not found: {dir}");
                continue;
            }

            pool.AddRange(Directory.GetFiles(dir, "*.iso", SearchOption.TopDirectoryOnly));
        }

        return pool
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> PickRandom(IReadOnlyList<string> pool, int count, int seed)
    {
        List<string> shuffled = [.. pool];
        Random rng = new(seed);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled.Take(Math.Max(0, count)).ToList();
    }

    /// <summary>Resolves the XISOSharp CLI exe: --cli, XISOSharp.exe beside the
    /// harness (fresh alias of the ProjectReference copy), XISOSharp.Cli.exe
    /// beside the harness (ProjectReference copy), then the Cli project's bin
    /// output.</summary>
    private static string ResolveCli(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        string? beside = new[] { "XISOSharp.exe", "XISOSharp.Cli.exe" }
            .Select(static n => Path.Combine(AppContext.BaseDirectory, n))
            .FirstOrDefault(static p => File.Exists(p));
        if (beside is not null)
        {
            return beside;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        foreach (string cfg in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(repoRoot, "XISOSharp.Cli", "bin", cfg, "net10.0", "XISOSharp.Cli.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "XISOSharp.Cli.exe");
    }

    /// <summary>Resolves an optional oracle exe: explicit path, else beside the harness; null when absent.</summary>
    private static ToolProcess? ResolveOptional(string? explicitPath, string fileName, int timeoutMs)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return new ToolProcess(explicitPath) { TimeoutMs = timeoutMs };
        }

        string beside = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(beside) ? new ToolProcess(beside) { TimeoutMs = timeoutMs } : null;
    }

    /// <summary>Resolves extract-xiso.exe: --exe, beside the harness, then beside the CWD.</summary>
    private static string ResolveOracle(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        string here = Path.Combine(AppContext.BaseDirectory, "extract-xiso.exe");
        if (File.Exists(here))
        {
            return here;
        }

        string local = Path.Combine(Directory.GetCurrentDirectory(), "extract-xiso.exe");
        return File.Exists(local) ? local : here;
    }

    private static void PrintSummary(BattleSession s)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine($"Battle Summary: {s.TotalIsos} ISO(s) in {s.Elapsed.TotalSeconds:F1}s | seed {s.Seed}");
        Console.WriteLine($"  CLI:    {s.CliVersion}");
        Console.WriteLine($"  Oracle: {s.OracleVersion}");
        Console.WriteLine(
            $"  Ops:    {s.TotalSubs} total | {s.PassedSubs} passed | {s.FailedSubs} failed | {s.SkippedSubs} skipped | {s.FailedIsos} ISO(s) with failures");
        ConsoleColor prev = Console.ForegroundColor;
        Console.ForegroundColor = s.FailedSubs > 0 ? ConsoleColor.Red : ConsoleColor.Green;
        Console.WriteLine(s.FailedSubs == 0
            ? "  RESULT: ALL CHECKS PASSED \u2713"
            : $"  RESULT: {s.FailedSubs} CHECK(S) FAILED \u2717");
        Console.ForegroundColor = prev;

        double cliTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.CliSeconds);
        double nativeTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.OracleSeconds);
        Console.Write("  Time:   ");
        prev = Console.ForegroundColor;
        Console.ForegroundColor = cliTotal <= nativeTotal ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"cli {cliTotal,8:F1}s");
        Console.ForegroundColor = prev;
        Console.Write("  vs  ");
        Console.ForegroundColor = nativeTotal < cliTotal ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"native {nativeTotal,8:F1}s");
        Console.ForegroundColor = prev;
        Console.WriteLine(nativeTotal > 0.05 ? $"  (cli {cliTotal / nativeTotal:F2}x native)" : string.Empty);
        Console.WriteLine("================================================================");
    }

    private static void CleanupWorkRoot(string workRoot, bool keep)
    {
        if (keep || !Directory.Exists(workRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(workRoot, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[WARN] Could not remove work root {workRoot}: {ex.Message}");
        }
    }
}
