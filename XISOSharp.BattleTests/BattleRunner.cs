using System.Diagnostics;

namespace XISOSharp.BattleTests;

/// <summary>Runs the CLI-vs-CLI battles (list, extract, rewrite) for one ISO.</summary>
internal static class BattleRunner
{
    /// <summary>Runs all requested ops for a single ISO and prints per-op verdicts.</summary>
    public static IsoResult RunIso(string iso, BattleOptions opt, ToolProcess cli, ToolProcess oracle, string workRoot,
        int index, int total)
    {
        Stopwatch sw = Stopwatch.StartNew();
        FileInfo fi = new(iso);
        IsoResult result = new() { FilePath = iso, FileName = fi.Name, FileSize = fi.Length };

        Console.WriteLine($"[{index}/{total}] {fi.Name} ({fi.Length / (1024.0 * 1024):F0} MB)");
        string stem = SafeStem(fi.Name);

        if (opt.Ops.Contains("list"))
        {
            SubResult sub = RunList(iso, cli, oracle);
            result.Subs.Add(sub);
            PrintSub(sub);
        }

        if (opt.Ops.Contains("extract"))
        {
            string work = Path.Combine(workRoot, stem + "_ext");
            SubResult sub = RunExtract(iso, cli, oracle, work);
            result.Subs.Add(sub);
            PrintSub(sub);
            Cleanup(work, opt.KeepWork);
        }

        if (opt.Ops.Contains("rewrite"))
        {
            string work = Path.Combine(workRoot, stem + "_rw");
            SubResult sub = RunRewrite(iso, cli, oracle, work);
            result.Subs.Add(sub);
            PrintSub(sub);
            Cleanup(work, opt.KeepWork);
        }

        sw.Stop();
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static void PrintSub(SubResult sub)
    {
        ConsoleColor prev = Console.ForegroundColor;
        Console.ForegroundColor = sub.Status switch
        {
            BattleStatus.Passed => ConsoleColor.Green,
            BattleStatus.Failed => ConsoleColor.Red,
            _ => ConsoleColor.Yellow,
        };
        Console.WriteLine($"  {sub.Op,-8} {sub.Status,-7} {sub.Seconds,7:F1}s  {sub.Detail.Split('\n').FirstOrDefault()?.Trim()}");
        Console.ForegroundColor = prev;
        foreach (string line in sub.Detail.Split('\n').Skip(1).Where(static l => !string.IsNullOrWhiteSpace(l)))
        {
            Console.WriteLine($"           {line.Trim()}");
        }
    }

    // ---- list ----------------------------------------------------------------

    /// <summary>Battle: -l entry lines must be identical between the two CLIs.</summary>
    private static SubResult RunList(string iso, ToolProcess cli, ToolProcess oracle)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            (int cCode, string cOut, string cErr) = cli.Run("-l", iso);
            (int oCode, string oOut, string oErr) = oracle.Run("-l", iso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "list", BattleStatus.Skipped, $"both tools failed: cli exit {cCode} ({First(cErr, cOut)}), native exit {oCode} ({First(oErr, oOut)})");
            }

            if (cCode != 0)
            {
                return Done(sw, "list", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr, cOut)} (native exit 0)");
            }

            if (oCode != 0)
            {
                return Done(sw, "list", BattleStatus.Failed, $"native exit {oCode}: {First(oErr, oOut)} (CLI exit 0)");
            }

            List<string> cEntries = ExtractEntries(cOut);
            List<string> oEntries = ExtractEntries(oOut);
            if (cEntries.Count == 0 && oEntries.Count == 0)
            {
                return Done(sw, "list", BattleStatus.Skipped, "no list entries parsed from either tool");
            }

            for (int i = 0; i < Math.Max(cEntries.Count, oEntries.Count); i++)
            {
                string? c = i < cEntries.Count ? cEntries[i] : null;
                string? o = i < oEntries.Count ? oEntries[i] : null;
                if (!string.Equals(c, o, StringComparison.Ordinal))
                {
                    return Done(sw, "list", BattleStatus.Failed,
                        $"entry {i + 1} differs:\n           cli: {c ?? "<none>"}\n           native: {o ?? "<none>"}");
                }
            }

            return Done(sw, "list", BattleStatus.Passed, $"{cEntries.Count} entries match");
        }
        catch (Exception ex)
        {
            return Done(sw, "list", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Keeps only file-entry lines: those starting with \ or / (banner/footer excluded).</summary>
    private static List<string> ExtractEntries(string stdout) =>
        stdout.Split('\n')
            .Select(static l => l.TrimEnd('\r').TrimEnd())
            .Where(static l => l.StartsWith('\\') || l.StartsWith('/'))
            .ToList();

    // ---- extract -------------------------------------------------------------

    /// <summary>Battle: -x -d trees must match (file set, per-file SHA-256, dir set).</summary>
    private static SubResult RunExtract(string iso, ToolProcess cli, ToolProcess oracle, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string csDir = Path.Combine(work, "cs");
        string exDir = Path.Combine(work, "exe");
        try
        {
            Directory.CreateDirectory(csDir);
            Directory.CreateDirectory(exDir);

            (int cCode, _, string cErr) = cli.Run("-x", "-d", csDir, iso);
            (int oCode, _, string oErr) = oracle.Run("-x", "-d", exDir, iso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "extract", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr)}, native: {First(oErr)}");
            }

            if (cCode != 0)
            {
                return Done(sw, "extract", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr)} (native exit 0)");
            }

            if (oCode != 0)
            {
                return Done(sw, "extract", BattleStatus.Failed, $"native exit {oCode}: {First(oErr)} (CLI exit 0)");
            }

            (bool equal, string detail) = CompareTrees(csDir, exDir);
            return Done(sw, "extract", equal ? BattleStatus.Passed : BattleStatus.Failed, detail);
        }
        catch (Exception ex)
        {
            return Done(sw, "extract", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Compares two extracted trees: relative file set, SHA-256 per file, and dir set.</summary>
    private static (bool Equal, string Detail) CompareTrees(string csDir, string exDir)
    {
        string[] csFiles = Directory.GetFiles(csDir, "*", SearchOption.AllDirectories);
        string[] exFiles = Directory.GetFiles(exDir, "*", SearchOption.AllDirectories);
        Dictionary<string, string> csMap = new(StringComparer.Ordinal);
        foreach (string f in csFiles)
        {
            csMap[Path.GetRelativePath(csDir, f)] = HashUtil.ComputeSha256(f);
        }

        Dictionary<string, string> exMap = new(StringComparer.Ordinal);
        foreach (string f in exFiles)
        {
            exMap[Path.GetRelativePath(exDir, f)] = HashUtil.ComputeSha256(f);
        }

        List<string> diffs = [];
        foreach (string onlyCs in csMap.Keys.Except(exMap.Keys, StringComparer.Ordinal).Take(5))
        {
            diffs.Add($"only in CLI tree: {onlyCs}");
        }

        foreach (string onlyEx in exMap.Keys.Except(csMap.Keys, StringComparer.Ordinal).Take(5))
        {
            diffs.Add($"only in native tree: {onlyEx}");
        }

        foreach (string both in csMap.Keys.Intersect(exMap.Keys, StringComparer.Ordinal))
        {
            if (!string.Equals(csMap[both], exMap[both], StringComparison.Ordinal))
            {
                diffs.Add($"hash mismatch: {both}");
                if (diffs.Count >= 5)
                {
                    break;
                }
            }
        }

        HashSet<string> csDirs = Directory
            .GetDirectories(csDir, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(csDir, d))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> exDirs = Directory
            .GetDirectories(exDir, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(exDir, d))
            .ToHashSet(StringComparer.Ordinal);

        if (diffs.Count == 0 && csDirs.Count == exDirs.Count && csDirs.All(exDirs.Contains))
        {
            return (true, $"{csMap.Count} files, {csDirs.Count} dirs, all SHA-256 match");
        }

        string head = $"{csMap.Count} files/{csDirs.Count} dirs (cli) vs {exMap.Count} files/{exDirs.Count} dirs (native)";
        if (diffs.Count == 0)
        {
            foreach (string onlyCs in csDirs.Except(exDirs, StringComparer.Ordinal).Take(3))
            {
                diffs.Add($"dir only in CLI tree: {onlyCs}");
            }

            foreach (string onlyEx in exDirs.Except(csDirs, StringComparer.Ordinal).Take(3))
            {
                diffs.Add($"dir only in native tree: {onlyEx}");
            }
        }

        return (false, head + "\n           " + string.Join("\n           ", diffs));
    }

    // ---- rewrite -------------------------------------------------------------

    /// <summary>Battle: -r -d outputs must match byte-for-byte (SHA-256).</summary>
    private static SubResult RunRewrite(string iso, ToolProcess cli, ToolProcess oracle, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            // Stage input copies: extract-xiso rewrites in place (renames the input
            // to *.old), so the source ISO on H: must never be handed over directly.
            string csIn = Path.Combine(work, "cs_in");
            string exIn = Path.Combine(work, "exe_in");
            string csOut = Path.Combine(work, "cs_out");
            string exOut = Path.Combine(work, "exe_out");
            Directory.CreateDirectory(csIn);
            Directory.CreateDirectory(exIn);
            Directory.CreateDirectory(csOut);
            Directory.CreateDirectory(exOut);
            string name = Path.GetFileName(iso);
            string csIso = Path.Combine(csIn, name);
            string exIso = Path.Combine(exIn, name);
            File.Copy(iso, csIso, true);
            File.Copy(iso, exIso, true);

            (int cCode, _, string cErr) = cli.Run("-r", "-d", csOut, csIso);
            (int oCode, _, string oErr) = oracle.Run("-r", "-d", exOut, exIso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "rewrite", BattleStatus.Skipped, $"both tools refused: cli: {First(cErr)}, native: {First(oErr)}");
            }

            if (cCode != 0)
            {
                return Done(sw, "rewrite", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr)} (native exit 0)");
            }

            if (oCode != 0)
            {
                return Done(sw, "rewrite", BattleStatus.Failed, $"native exit {oCode}: {First(oErr)} (CLI exit 0)");
            }

            string? csOutIso = FindRewriteOutput(work, csOut, csIn);
            string? exOutIso = FindRewriteOutput(work, exOut, exIn);
            if (csOutIso is null || exOutIso is null)
            {
                return Done(sw, "rewrite", BattleStatus.Failed, $"rewritten ISO not found: cli={csOutIso ?? "null"}, native={exOutIso ?? "null"}");
            }

            string csHash = HashUtil.ComputeSha256(csOutIso);
            string exHash = HashUtil.ComputeSha256(exOutIso);
            long csLen = new FileInfo(csOutIso).Length;
            long exLen = new FileInfo(exOutIso).Length;
            if (string.Equals(csHash, exHash, StringComparison.Ordinal))
            {
                return Done(sw, "rewrite", BattleStatus.Passed, $"SHA256 {csHash} ({csLen} bytes)");
            }

            return Done(sw, "rewrite", BattleStatus.Failed,
                $"SHA256 mismatch: cli {csHash} ({csLen} bytes) vs native {exHash} ({exLen} bytes)");
        }
        catch (Exception ex)
        {
            return Done(sw, "rewrite", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates a rewrite output: first under <paramref name="outDir"/>; otherwise the
    /// newest *.iso under <paramref name="workDir"/> excluding *.old backups and the
    /// staged input dir (the oracle may write beside the input instead of -d).
    /// </summary>
    private static string? FindRewriteOutput(string workDir, string outDir, string inDir)
    {
        string? local = Directory.GetFiles(outDir, "*.iso", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
        if (local is not null)
        {
            return local;
        }

        string inPrefix = inDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        return Directory.GetFiles(workDir, "*.iso", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) &&
                        !f.StartsWith(inPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    // ---- helpers ---------------------------------------------------------------

    private static SubResult Done(Stopwatch sw, string op, BattleStatus status, string detail)
    {
        sw.Stop();
        return new SubResult(op, status, detail, sw.Elapsed.TotalSeconds);
    }

    private static string First(params string[] texts) =>
        texts.Select(static t => t.Split('\n').FirstOrDefault(static l => !string.IsNullOrWhiteSpace(l))?.Trim())
            .FirstOrDefault(static s => !string.IsNullOrEmpty(s)) ?? "(no output)";

    private static string SafeStem(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string safe = new(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length > 60 ? safe[..60] : safe;
    }

    private static void Cleanup(string work, bool keep)
    {
        if (keep || !Directory.Exists(work))
        {
            return;
        }

        TryDelete(work);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // Best effort; --keep lets the user inspect anything that lingers.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
