using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XISOSharp.BattleTests;

/// <summary>Writes the battle report (txt + json) under BattleReports\.</summary>
internal static class BattleReport
{
    public static void Write(BattleSession s, IReadOnlyList<string> picked)
    {
        try
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "BattleReports");
            Directory.CreateDirectory(outDir);
            // Sub-second + PID component so concurrent runs never overwrite each
            // other's reports (second-granularity stamps collide).
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                           + "_" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            string txtPath = Path.Combine(outDir, $"battle_{stamp}.txt");
            string jsonPath = Path.Combine(outDir, $"battle_{stamp}.json");

            File.WriteAllText(txtPath, BuildText(s, picked, stamp), new UTF8Encoding(false));
            Console.WriteLine($"Report: {txtPath}");
            File.WriteAllText(jsonPath, BuildJson(s, picked, stamp), new UTF8Encoding(false));
            Console.WriteLine($"        {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to write reports: {ex.Message}");
        }
    }

    private static string BuildText(BattleSession s, IReadOnlyList<string> picked, string stamp)
    {
        StringBuilder w = new();
        w.AppendLine(CultureInfo.InvariantCulture, $"XISOSharp Battle Report {stamp}");
        w.AppendLine(CultureInfo.InvariantCulture, $"CLI:    {s.CliPath} | {s.CliVersion}");
        w.AppendLine(CultureInfo.InvariantCulture, $"Oracle: {s.OraclePath} | {s.OracleVersion}");
        w.AppendLine(CultureInfo.InvariantCulture,
            $"xdvdfs: {(s.XdvdfsPath.Length > 0 ? s.XdvdfsPath : "(not found)")} | {s.XdvdfsVersion}");
        w.AppendLine(CultureInfo.InvariantCulture,
            $"xboxkit: {(s.XboxkitPath.Length > 0 ? s.XboxkitPath : "(not found)")} | {s.XboxkitVersion}");
        w.AppendLine(CultureInfo.InvariantCulture,
            $"Seed: {s.Seed} | Ops: {string.Join(", ", s.Ops)} | Work: {s.WorkRoot}");
        w.AppendLine(CultureInfo.InvariantCulture, $"ISOs ({picked.Count}):");
        foreach (string iso in picked)
        {
            w.AppendLine(CultureInfo.InvariantCulture, $"  {iso}");
        }

        w.AppendLine(
            CultureInfo.InvariantCulture,
            $"Summary: {s.PassedSubs}/{s.TotalSubs} checks passed, {s.FailedSubs} failed, {s.SkippedSubs} skipped in {s.Elapsed.TotalSeconds:F1}s");
        double cliTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.CliSeconds);
        double nativeTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.OracleSeconds);
        string timeLine = $"Time: cli {cliTotal:F1}s vs native {nativeTotal:F1}s" +
                          (nativeTotal > 0.05 ? $" (cli {cliTotal / nativeTotal:F2}x native)" : string.Empty);
        w.AppendLine(timeLine);
        w.AppendLine();
        foreach (IsoResult f in s.IsoResults)
        {
            w.AppendLine(
                CultureInfo.InvariantCulture,
                $"{f.FileName} ({f.FileSize} bytes) - {(f.HasFailures ? "FAIL" : f.AllPassed ? "PASS" : "OK/SKIP")} {f.Seconds:F1}s");
            foreach (SubResult sub in f.Subs)
            {
                w.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {sub.Op,-8} {sub.Status,-7} {sub.Seconds,8:F1}s (cli {sub.CliSeconds,7:F1}s / native {sub.OracleSeconds,7:F1}s)  {sub.Detail.Replace('\n', ' ').Trim()}");
            }

            w.AppendLine();
        }

        return w.ToString();
    }

    private static string BuildJson(BattleSession s, IReadOnlyList<string> picked, string stamp) =>
        JsonSerializer.Serialize(new
        {
            timestamp = stamp,
            seed = s.Seed,
            ops = s.Ops,
            cli = new { path = s.CliPath, version = s.CliVersion },
            oracle = new { path = s.OraclePath, version = s.OracleVersion },
            xdvdfs = new { path = s.XdvdfsPath, version = s.XdvdfsVersion },
            xboxkit = new { path = s.XboxkitPath, version = s.XboxkitVersion },
            workRoot = s.WorkRoot,
            totalIsos = s.TotalIsos,
            failedIsos = s.FailedIsos,
            totalChecks = s.TotalSubs,
            passedChecks = s.PassedSubs,
            failedChecks = s.FailedSubs,
            skippedChecks = s.SkippedSubs,
            elapsedSeconds = s.Elapsed.TotalSeconds,
            cliSecondsTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.CliSeconds),
            nativeSecondsTotal = s.IsoResults.SelectMany(static r => r.Subs).Sum(static x => x.OracleSeconds),
            isos = picked,
            results = s.IsoResults.Select(f => new
            {
                f.FileName,
                f.FilePath,
                f.FileSize,
                f.Seconds,
                f.AllPassed,
                subs = f.Subs.Select(sub => new
                {
                    sub.Op,
                    status = sub.Status.ToString(),
                    sub.Detail,
                    sub.Seconds,
                    sub.CliSeconds,
                    sub.OracleSeconds,
                }),
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
}
