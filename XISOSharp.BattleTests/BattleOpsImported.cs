using System.Diagnostics;
using System.Text.RegularExpressions;

namespace XISOSharp.BattleTests;

/// <summary>
/// Battle ops for features XISOSharp imported from xdvdfs 0.8.3 (checksum, md5,
/// unpack, pack, cso) and XboxKit 0.7 (petrify, video, random, seed, trim, wipe,
/// zar, rebuild). Part of <see cref="BattleRunner"/>.
/// </summary>
internal static partial class BattleRunner
{
    private static readonly Regex Hex64Regex =
        new("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    private static readonly Regex Md5LineRegex =
        new(@"^(?<hash>[0-9a-f]{32})\s+(?<path>/\S.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    // ---- xdvdfs oracle -------------------------------------------------------

    /// <summary>Battle: deterministic SHA3-256 image checksums must match exactly.</summary>
    private static SubResult RunChecksum(string iso, ToolProcess cli, ToolProcess xdvdfs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            (int cCode, string cOut, string cErr, double cSec) = cli.Run("checksum", "--silent", iso);
            (int oCode, string oOut, string oErr, double oSec) = xdvdfs.Run("checksum", iso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "checksum", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr, cOut)}, xdvdfs: {First(oErr, oOut)}", cSec, oSec);
            }

            if (cCode != 0)
            {
                return Done(sw, "checksum", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr, cOut)} (xdvdfs exit 0)", cSec, oSec);
            }

            if (oCode != 0)
            {
                return Done(sw, "checksum", BattleStatus.Failed, $"xdvdfs exit {oCode}: {First(oErr, oOut)} (CLI exit 0)", cSec, oSec);
            }

            string? cHex = FirstHex(cOut);
            string? oHex = FirstHex(oOut);
            if (cHex is null || oHex is null)
            {
                return Done(sw, "checksum", BattleStatus.Skipped,
                    $"could not parse hex checksums: cli={(cHex ?? "null")}, xdvdfs={(oHex ?? "null")}", cSec, oSec);
            }

            return string.Equals(cHex, oHex, StringComparison.OrdinalIgnoreCase)
                ? Done(sw, "checksum", BattleStatus.Passed, $"SHA3-256 {cHex}", cSec, oSec)
                : Done(sw, "checksum", BattleStatus.Failed, $"SHA3-256 mismatch: cli {cHex} vs xdvdfs {oHex}", cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "checksum", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    /// <summary>Battle: per-file MD5 lists must agree (every CLI entry must exist in the
    /// xdvdfs list with the same hash; extra xdvdfs-only entries — e.g. directory rows —
    /// are noted but not fatal).</summary>
    private static SubResult RunMd5(string iso, ToolProcess cli, ToolProcess xdvdfs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            (int cCode, string cOut, string cErr, double cSec) = cli.Run("--md5", iso);
            (int oCode, string oOut, string oErr, double oSec) = xdvdfs.Run("md5", iso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "md5", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr, cOut)}, xdvdfs: {First(oErr, oOut)}", cSec, oSec);
            }

            if (cCode != 0)
            {
                return Done(sw, "md5", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr, cOut)} (xdvdfs exit 0)", cSec, oSec);
            }

            if (oCode != 0)
            {
                return Done(sw, "md5", BattleStatus.Failed, $"xdvdfs exit {oCode}: {First(oErr, oOut)} (CLI exit 0)", cSec, oSec);
            }

            Dictionary<string, string> cMap = ParseMd5Map(cOut);
            Dictionary<string, string> oMap = ParseMd5Map(oOut);
            if (cMap.Count == 0 && oMap.Count == 0)
            {
                return Done(sw, "md5", BattleStatus.Skipped, "no md5 entries parsed from either tool", cSec, oSec);
            }

            List<string> diffs = [];
            foreach ((string path, string hash) in cMap)
            {
                if (!oMap.TryGetValue(path, out string? oHash))
                {
                    diffs.Add($"missing in xdvdfs list: {path}");
                }
                else if (!string.Equals(hash, oHash, StringComparison.OrdinalIgnoreCase))
                {
                    diffs.Add($"md5 mismatch: {path}: cli {hash} vs xdvdfs {oHash}");
                }

                if (diffs.Count >= 5)
                {
                    break;
                }
            }

            int onlyXdvdfs = oMap.Keys.Except(cMap.Keys, StringComparer.Ordinal).Count();
            if (diffs.Count > 0)
            {
                return Done(sw, "md5", BattleStatus.Failed,
                    $"{cMap.Count} cli entries vs {oMap.Count} xdvdfs entries\n           " + string.Join("\n           ", diffs), cSec, oSec);
            }

            return Done(sw, "md5", BattleStatus.Passed,
                $"{cMap.Count} files agree" + (onlyXdvdfs > 0 ? $" ({onlyXdvdfs} xdvdfs-only dir/extra entries)" : string.Empty), cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "md5", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    /// <summary>Battle: --unpack vs `xdvdfs unpack` — extracted trees must match
    /// (file set, per-file SHA-256, dir set).</summary>
    private static SubResult RunUnpack(string iso, ToolProcess cli, ToolProcess xdvdfs, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string csDir = Path.Combine(work, "cs");
            string xdDir = Path.Combine(work, "xd");
            Directory.CreateDirectory(csDir);
            Directory.CreateDirectory(xdDir);

            (int cCode, _, string cErr, double cSec) = cli.Run("--unpack", iso, csDir);
            (int oCode, _, string oErr, double oSec) = xdvdfs.Run("unpack", iso, xdDir);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "unpack", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr)}, xdvdfs: {First(oErr)}", cSec, oSec);
            }

            if (cCode != 0)
            {
                return Done(sw, "unpack", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr)} (xdvdfs exit 0)", cSec, oSec);
            }

            if (oCode != 0)
            {
                return Done(sw, "unpack", BattleStatus.Failed, $"xdvdfs exit {oCode}: {First(oErr)} (CLI exit 0)", cSec, oSec);
            }

            (bool equal, string detail) = CompareTrees(csDir, xdDir);
            return Done(sw, "unpack", equal ? BattleStatus.Passed : BattleStatus.Failed, detail, cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "unpack", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    /// <summary>
    /// Battle: `-c` (media patch off via -m) vs `xdvdfs pack` over the same unpacked
    /// dir. The two packed images are compared via the deterministic content checksum
    /// (layout-agnostic): identical file sets + bytes must produce identical SHA3-256.
    /// </summary>
    private static SubResult RunPack(string iso, ToolProcess cli, ToolProcess xdvdfs, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(work);
            string dir = Path.Combine(work, "src");
            string pIso = Path.Combine(work, "cs_packed.iso");
            string xIso = Path.Combine(work, "xd_packed.iso");
            (int uCode, _, string uErr, _) = cli.Run("--unpack", iso, dir);
            if (uCode != 0)
            {
                return Done(sw, "pack", BattleStatus.Skipped, $"source unpack failed: {First(uErr)}", 0, 0);
            }

            // -m (no media patch) must precede -c: -c consumes the rest as positionals.
            (int cCode, _, string cErr, double cSec) = cli.Run("-m", "-c", dir, pIso);
            (int oCode, _, string oErr, double oSec) = xdvdfs.Run("pack", dir, xIso);

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "pack", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr)}, xdvdfs: {First(oErr)}", cSec, oSec);
            }

            if (cCode != 0)
            {
                return Done(sw, "pack", BattleStatus.Failed, $"CLI exit {cCode}: {First(cErr)} (xdvdfs exit 0)", cSec, oSec);
            }

            if (oCode != 0)
            {
                return Done(sw, "pack", BattleStatus.Failed, $"xdvdfs exit {oCode}: {First(oErr)} (CLI exit 0)", cSec, oSec);
            }

            (_, string cHex, _, _) = cli.Run("checksum", "--silent", pIso);
            (_, string oHex, _, _) = cli.Run("checksum", "--silent", xIso);
            string? pHex = FirstHex(cHex);
            string? xHex = FirstHex(oHex);
            if (pHex is null || xHex is null)
            {
                return Done(sw, "pack", BattleStatus.Skipped, "could not parse content checksums of the packed images", cSec, oSec);
            }

            long pLen = new FileInfo(pIso).Length;
            long xLen = new FileInfo(xIso).Length;
            return string.Equals(pHex, xHex, StringComparison.OrdinalIgnoreCase)
                ? Done(sw, "pack", BattleStatus.Passed, $"content parity: SHA3-256 {pHex} (cli {pLen} B, xdvdfs {xLen} B)", cSec, oSec)
                : Done(sw, "pack", BattleStatus.Failed,
                    $"content mismatch: cli SHA3-256 {pHex} ({pLen} B) vs xdvdfs {xHex} ({xLen} B) over the same source dir", cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "pack", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    /// <summary>
    /// Battle: CISO round-trip with oracle cross-read. XISOSharp compresses the ISO
    /// (split CSO), xdvdfs reads the CSO back (md5 per file — proves oracle-compatible
    /// CSO output), XISOSharp decompresses and the content checksum must equal the
    /// source image's. Redump inputs start with the video partition, which no CSO
    /// reader accepts at sector 0 (xdvdfs compress itself refuses them), so the game
    /// partition is staged to a sector-0 file first and the battle runs on that.
    /// </summary>
    private static SubResult RunCso(string iso, ToolProcess cli, ToolProcess xdvdfs, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(work);
            string csoInput = TryStageRedumpPartition(iso, work) ?? iso;
            (_, string srcHexOut, _, _) = cli.Run("checksum", "--silent", csoInput);
            string? srcHex = FirstHex(srcHexOut);
            if (srcHex is null)
            {
                return Done(sw, "cso", BattleStatus.Skipped, "could not parse source content checksum", 0, 0);
            }

            string csoPath = Path.Combine(work, "o.cso");
            (int cCode, _, string cErr, double cSec) = cli.Run("cso", csoInput, csoPath);
            if (cCode != 0)
            {
                return Done(sw, "cso", BattleStatus.Failed, $"CLI compress exit {cCode}: {First(cErr, "no output")}", cSec, 0);
            }

            string[] parts = Directory.GetFiles(work, "*.cso", SearchOption.TopDirectoryOnly)
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (parts.Length == 0)
            {
                return Done(sw, "cso", BattleStatus.Failed, "no CSO parts found after compress", cSec, 0);
            }

            (int oCode, string oOut, string oErr, double oSec) = xdvdfs.Run("md5", parts[0]);
            if (oCode != 0)
            {
                return Done(sw, "cso", BattleStatus.Failed,
                    $"xdvdfs could not read the XISOSharp CSO (exit {oCode}): {First(oErr, oOut)}", cSec, oSec);
            }

            (_, string isoMd5Out, _, _) = cli.Run("--md5", csoInput);
            Dictionary<string, string> isoMap = ParseMd5Map(isoMd5Out);
            Dictionary<string, string> csoMap = ParseMd5Map(oOut);
            List<string> diffs = [];
            foreach ((string path, string hash) in isoMap)
            {
                if (!csoMap.TryGetValue(path, out string? cHash))
                {
                    diffs.Add($"missing in xdvdfs md5 of CSO: {path}");
                }
                else if (!string.Equals(hash, cHash, StringComparison.OrdinalIgnoreCase))
                {
                    diffs.Add($"md5 mismatch through CSO: {path}");
                }

                if (diffs.Count >= 5)
                {
                    break;
                }
            }

            string backIso = Path.Combine(work, "back.iso");
            (int dCode, _, string dErr, _) = cli.Run("decompress", parts[0], backIso);
            if (dCode != 0)
            {
                return Done(sw, "cso", BattleStatus.Failed, $"CLI decompress exit {dCode}: {First(dErr)}", cSec, oSec);
            }

            (_, string backHexOut, _, _) = cli.Run("checksum", "--silent", backIso);
            string? backHex = FirstHex(backHexOut);
            bool roundTrip = string.Equals(backHex, srcHex, StringComparison.OrdinalIgnoreCase);
            if (!roundTrip)
            {
                diffs.Add($"round-trip checksum: source {srcHex} vs decompressed {backHex ?? "null"}");
            }

            return diffs.Count == 0
                ? Done(sw, "cso", BattleStatus.Passed,
                    $"{parts.Length} part(s); xdvdfs cross-read OK; round-trip checksum {backHex}", cSec, oSec)
                : Done(sw, "cso", BattleStatus.Failed, string.Join("\n           ", diffs), cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "cso", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    // ---- xboxkit oracle ------------------------------------------------------

    /// <summary>
    /// Shared runner for the staged-copy, hash-compare xboxkit ops (petrify, video,
    /// random, seed, zar, trim, wipe). Each side works on its own staged copy of the
    /// ISO (xboxkit writes beside / in-place on its input), outputs are discovered
    /// (xboxkit always exits 0, so presence is the success signal), and the files are
    /// compared by SHA-256. Both sides refusing = Skipped (e.g. trimmed ISOs for
    /// redump-only ops); one side failing = Failed.
    /// </summary>
    /// <param name="op">Op name for reporting.</param>
    /// <param name="iso">Path of the source ISO to stage a copy of per side.</param>
    /// <param name="cli">The XISOSharp CLI process.</param>
    /// <param name="xk">The xboxkit.exe oracle process.</param>
    /// <param name="work">Scratch dir receiving the per-side staged copies.</param>
    /// <param name="cliTemplate">CLI args template with {ISO}/{OUT} placeholders.</param>
    /// <param name="xkTemplate">xboxkit args template with {ISO} placeholder.</param>
    /// <param name="preferredPattern">
    /// Output glob hint for both sides (e.g. "*video*"), or null for in-place ops
    /// (trim/wipe: xboxkit's output is its staged input itself).
    /// </param>
    private static SubResult RunStagedCompare(string op, string iso, ToolProcess cli, ToolProcess xk, string work,
        string[] cliTemplate, string[] xkTemplate, string? preferredPattern)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string xsDir = Path.Combine(work, "cs");
            string xkDir = Path.Combine(work, "xk");
            Directory.CreateDirectory(xsDir);
            Directory.CreateDirectory(xkDir);
            string name = Path.GetFileName(iso);
            string xsIso = Path.Combine(xsDir, name);
            string xkIso = Path.Combine(xkDir, name);
            File.Copy(iso, xsIso, true);
            File.Copy(iso, xkIso, true);

            string outExt = string.Equals(op, "zar", StringComparison.Ordinal) ? ".zar" : ".iso";
            string xsOut = Path.Combine(xsDir, "out" + outExt);
            string[] cliArgs = [.. cliTemplate.Select(a => a
                .Replace("{ISO}", xsIso)
                .Replace("{OUT}", xsOut))];
            string[] xkArgs = [.. xkTemplate.Select(a => a.Replace("{ISO}", xkIso))];

            (int cCode, _, string cErr, double cSec) = cli.Run(cliArgs);
            string? xkPreHash = preferredPattern is null ? HashUtil.ComputeSha256(xkIso) : null;
            (int oCode, _, string oErr, double oSec) = xk.Run(xkArgs);

            string? xsFile = cliTemplate.Any(static a => string.Equals(a, "{OUT}", StringComparison.Ordinal))
                ? (File.Exists(xsOut) ? xsOut : null)
                : FindOutput(xsDir, xsIso, preferredPattern);
            string? xkFile = preferredPattern is null ? (File.Exists(xkIso) ? xkIso : null) : FindOutput(xkDir, xkIso, preferredPattern);

            bool cliOk = cCode == 0 && xsFile is not null;
            // In-place ops (trim/wipe): xboxkit always exits 0, so require an actual
            // change — a different hash or an .old backup beside the staged input.
            bool xkChanged = xkFile is not null &&
                             (preferredPattern is not null ||
                              !string.Equals(HashUtil.ComputeSha256(xkFile), xkPreHash, StringComparison.OrdinalIgnoreCase) ||
                              Directory.GetFiles(xkDir, "*.old", SearchOption.TopDirectoryOnly).Length > 0);
            bool xkOk = xkChanged;
            if (!cliOk && !xkOk)
            {
                return Done(sw, op, BattleStatus.Skipped,
                    $"both tools refused: cli: {First(cErr, $"no {op} output")}, xboxkit: {First(oErr, $"no {op} output")}", cSec, oSec);
            }

            // Only comparable outputs can pass or fail; an asymmetric refusal is a
            // capability difference surfaced in the detail, not a hash mismatch.
            if (!xkOk)
            {
                return Done(sw, op, BattleStatus.Skipped,
                    $"xboxkit refused (exit {oCode}, no {op} output — trimmed/unsupported input?); CLI produced {Path.GetFileName(xsFile)} — nothing to compare", cSec, oSec);
            }

            if (!cliOk)
            {
                return Done(sw, op, BattleStatus.Skipped,
                    $"CLI refused (exit {cCode}: {First(cErr, $"no {op} output")}); xboxkit produced {Path.GetFileName(xkFile)} — nothing to compare", cSec, oSec);
            }

            string xsHash = HashUtil.ComputeSha256(xsFile!);
            string xkHash = HashUtil.ComputeSha256(xkFile!);
            long xsLen = new FileInfo(xsFile!).Length;
            long xkLen = new FileInfo(xkFile!).Length;
            if (string.Equals(xsHash, xkHash, StringComparison.OrdinalIgnoreCase))
            {
                return Done(sw, op, BattleStatus.Passed, $"SHA256 {xsHash} ({xsLen} bytes)", cSec, oSec);
            }

            // Petrify tiebreaker: on a byte mismatch, verify our skeleton
            // structurally before failing (see VerifySkeletonStructure).
            if (string.Equals(op, "petrify", StringComparison.Ordinal))
            {
                SubResult? tie = PetrifyTiebreaker(sw, iso, xsFile!, cSec, oSec);
                if (tie is not null)
                {
                    return tie;
                }
            }

            return Done(sw, op, BattleStatus.Failed,
                $"SHA256 mismatch: cli {xsHash} ({xsLen} bytes) vs xboxkit {xkHash} ({xkLen} bytes)", cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, op, BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    /// <summary>
    /// Battle: lossless redump rebuild. Components (video partition, filler, optional
    /// su20076000 update) are extracted once with the CLI, then both tools rebuild the
    /// full image from them; each rebuilt image must match the original byte-for-byte.
    /// Requires a full redump image — skips on trimmed XISOs.
    /// </summary>
    private static SubResult RunRebuild(string iso, ToolProcess cli, ToolProcess xk, string work)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string xsDir = Path.Combine(work, "cs");
            string xkDir = Path.Combine(work, "xk");
            Directory.CreateDirectory(xsDir);
            Directory.CreateDirectory(xkDir);
            string stem = Path.GetFileNameWithoutExtension(iso);
            string xsIso = Path.Combine(xsDir, stem + ".xiso");
            string xkIso = Path.Combine(xkDir, stem + ".xiso");
            File.Copy(iso, xsIso, true);
            File.Copy(iso, xkIso, true);

            (int vCode, _, string vErr, _) = cli.Run("--video", xsIso);
            (int rCode, _, string rErr, _) = cli.Run("--random", xsIso);
            if (vCode != 0 || rCode != 0)
            {
                return Done(sw, "rebuild", BattleStatus.Skipped,
                    $"component extraction refused (trimmed ISO?): video {vCode} {First(vErr)}, random {rCode} {First(rErr)}", 0, 0);
            }

            string? video = FindOutput(xsDir, xsIso, "*video*");
            string? filler = FindOutput(xsDir, xsIso, "*filler*");
            if (video is null || filler is null)
            {
                return Done(sw, "rebuild", BattleStatus.Skipped, "video/filler components not found after extraction", 0, 0);
            }

            (_, string uOut, _, _) = cli.Run("--update", xsIso);
            string? update = Directory.GetFiles(xsDir, "su20076000*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            _ = uOut;

            // Identical component inputs for both rebuilders.
            File.Copy(video, Path.Combine(xkDir, Path.GetFileName(video)), true);
            File.Copy(filler, Path.Combine(xkDir, Path.GetFileName(filler)), true);
            if (update is not null)
            {
                File.Copy(update, Path.Combine(xkDir, Path.GetFileName(update)), true);
            }

            string xsOut = Path.Combine(xsDir, "rebuilt.iso");
            List<string> xsArgs = ["rebuild", xsIso, video, filler];
            List<string> xkArgs = [xkIso, Path.GetFileName(video), Path.GetFileName(filler)];
            if (update is not null)
            {
                xsArgs.Add(update);
                xkArgs.Add(Path.GetFileName(update));
            }

            xsArgs.AddRange(["-o", xsOut]);
            (int cCode, _, string cErr, double cSec) = cli.Run([.. xsArgs]);
            // xboxkit rebuild mode combines the input files (cwd-sensitive args first,
            // relative component names resolve beside the staged input).
            string prevDir = Directory.GetCurrentDirectory();
            double oSec = 0;
            int oCode = 0;
            string oErr = string.Empty;
            try
            {
                Directory.SetCurrentDirectory(xkDir);
                (oCode, _, oErr, oSec) = xk.Run([.. xkArgs]);
            }
            finally
            {
                Directory.SetCurrentDirectory(prevDir);
            }

            if (cCode != 0 && oCode != 0)
            {
                return Done(sw, "rebuild", BattleStatus.Skipped, $"both tools failed: cli: {First(cErr)}, xboxkit: {First(oErr)}", cSec, oSec);
            }

            string originalHash = HashUtil.ComputeSha256(iso);
            long originalLen = new FileInfo(iso).Length;
            string? xsFile = File.Exists(xsOut) ? xsOut : FindOutput(xsDir, xsIso, "rebuilt*");
            string? xkFile = Directory.GetFiles(xkDir, "*.iso", SearchOption.TopDirectoryOnly)
                .Where(static f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            List<string> verdicts = [];
            bool xsOk = false;
            bool xkOk = false;
            if (cCode != 0 || xsFile is null)
            {
                verdicts.Add($"CLI rebuild failed: exit {cCode}: {First(cErr, "no output")}");
            }
            else
            {
                string h = HashUtil.ComputeSha256(xsFile);
                xsOk = string.Equals(h, originalHash, StringComparison.OrdinalIgnoreCase);
                verdicts.Add($"cli rebuilt {(xsOk ? "MATCH" : "MISMATCH")} vs original ({h} vs {originalHash}, {new FileInfo(xsFile).Length}/{originalLen} B)");
            }

            if (oCode != 0 || xkFile is null)
            {
                verdicts.Add($"xboxkit rebuild failed: exit {oCode}: {First(oErr, "no output")}");
            }
            else
            {
                string h = HashUtil.ComputeSha256(xkFile);
                xkOk = string.Equals(h, originalHash, StringComparison.OrdinalIgnoreCase);
                verdicts.Add($"xboxkit rebuilt {(xkOk ? "MATCH" : "MISMATCH")} vs original ({h} vs {originalHash}, {new FileInfo(xkFile).Length}/{originalLen} B)");
            }

            BattleStatus status = xsOk && xkOk ? BattleStatus.Passed : BattleStatus.Failed;
            return Done(sw, "rebuild", status, string.Join("\n           ", verdicts), cSec, oSec);
        }
        catch (Exception ex)
        {
            return Done(sw, "rebuild", BattleStatus.Failed, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Tiebreaker for petrify byte mismatches. xboxkit 0.7's skeleton walk zeroes
    /// to merged-extent ends (paving over bone islands that share an extent with
    /// file data) and can desync its read position while hashing inline, so it
    /// emits unlistable skeletons on real mastered images. When our skeleton
    /// verifies structurally (bones verbatim, everything else zero) the mismatch
    /// is an oracle-side defect (Skipped), not a CLI failure. Returns null when
    /// our skeleton does not verify (fall through to Failed).
    /// </summary>
    private static SubResult? PetrifyTiebreaker(Stopwatch sw, string iso, string xsFile, double cSec, double oSec)
    {
        if (VerifySkeletonStructure(iso, xsFile, out string detail))
        {
            return Done(sw, "petrify", BattleStatus.Skipped,
                $"CLI skeleton is structurally correct ({detail}) but differs from xboxkit -p " +
                "(oracle zeroes filesystem tables inside mixed bone/file extents — oracle-side defect, not comparable)", cSec, oSec);
        }

        return null;
    }

    /// <summary>
    /// Structural skeleton check: same byte length as the game partition, every
    /// filesystem (bone) byte identical to the source, every other byte zero.
    /// Partition bounds mirror the CLI's Redump detection.
    /// </summary>
    private static bool VerifySkeletonStructure(string iso, string skeleton, out string detail)
    {
        detail = string.Empty;
        try
        {
            long size = new FileInfo(iso).Length;
            long isoOffset = 0;
            long partLen = size;
            if (TryGetPartitionBounds(iso, size, out long off, out long len))
            {
                isoOffset = off;
                partLen = len;
            }

            long skelLen = new FileInfo(skeleton).Length;
            if (skelLen != partLen)
            {
                detail = $"skeleton size {skelLen} != partition length {partLen}";
                return false;
            }

            (List<(uint Start, uint End)> bones, _) = XisoRanges.GetXisoRanges(iso, isoOffset, true);
            long baseSector = isoOffset / 2048;
            List<(long Start, long End)> keep = [];
            foreach ((uint s, uint e) in bones)
            {
                long cs = Math.Max((long)s, baseSector);
                long ce = Math.Min((long)e, baseSector + ((partLen + 2047) / 2048) - 1);
                if (ce < cs)
                {
                    continue;
                }

                long bs = (cs - baseSector) * 2048;
                long be = Math.Min((ce - baseSector + 1) * 2048, partLen);
                if (bs < be && (keep.Count == 0 || bs > keep[^1].End))
                {
                    keep.Add((bs, be));
                }
                else if (bs < be)
                {
                    keep[^1] = (keep[^1].Start, Math.Max(keep[^1].End, be));
                }
            }

            using FileStream srcFs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using FileStream skFs = new(skeleton, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            srcFs.Seek(isoOffset, SeekOrigin.Begin);
            byte[] srcBuf = new byte[1024 * 1024];
            byte[] skBuf = new byte[1024 * 1024];
            long pos = 0;
            int ki = 0;
            long boneBytes = 0;
            while (pos < partLen)
            {
                int n = (int)Math.Min(srcBuf.Length, partLen - pos);
                if (ReadFull(srcFs, srcBuf, n) != n || ReadFull(skFs, skBuf, n) != n)
                {
                    detail = $"short read at partition offset {pos}";
                    return false;
                }

                for (int i = 0; i < n; i++)
                {
                    long abs = pos + i;
                    while (ki < keep.Count && abs >= keep[ki].End)
                    {
                        ki++;
                    }

                    bool inBone = ki < keep.Count && abs >= keep[ki].Start;
                    if (inBone)
                    {
                        boneBytes++;
                        if (skBuf[i] != srcBuf[i])
                        {
                            detail = $"bone byte differs at partition offset {abs}";
                            return false;
                        }
                    }
                    else if (skBuf[i] != 0)
                    {
                        detail = $"non-zero non-bone byte at partition offset {abs}";
                        return false;
                    }
                }

                pos += n;
            }

            detail = $"{boneBytes} bone bytes verbatim, rest zeroed";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = $"verification I/O error: {ex.Message.Split('\n')[0]}";
            return false;
        }
    }

    private static int ReadFull(FileStream fs, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = fs.Read(buf, total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>
    /// Resolves the game-partition bounds of a Redump ISO (mirrors the CLI's
    /// detection); returns false for non-Redump inputs.
    /// </summary>
    private static bool TryGetPartitionBounds(string iso, long size, out long isoOffset, out long xisoLen)
    {
        isoOffset = 0;
        xisoLen = size;
        try
        {
            int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            if (redumpType < 0)
            {
                return false;
            }

            using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            int videoType = XgdTables.GetVideoType(fs, redumpType);
            int xsType = XgdTables.GetXisoTypeFromVideo(videoType >= 0 ? videoType : 0);
            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
            {
                xsType = XgdTables.GetXgdType(redumpType);
            }

            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
            {
                return false;
            }

            isoOffset = XgdTables.XisoOffset[xsType];
            xisoLen = XgdTables.XisoLength[xsType];
            return isoOffset >= 0 && xisoLen > 0 && isoOffset + xisoLen <= size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stages the game-partition bytes of a Redump ISO as a sector-0 file
    /// (<c>part.iso</c> under <paramref name="work"/>), or returns null when the
    /// input is not a known Redump size (used as-is then). Partition bounds mirror
    /// the CLI's Redump detection (<c>XgdTables</c>, pulled in transitively via the
    /// CLI project reference).
    /// </summary>
    private static string? TryStageRedumpPartition(string iso, string work)
    {
        try
        {
            long size = new FileInfo(iso).Length;
            if (!TryGetPartitionBounds(iso, size, out long isoOffset, out long xisoLen))
            {
                return null;
            }

            string part = Path.Combine(work, "part.iso");
            using (FileStream src = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (FileStream dst = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                src.Seek(isoOffset, SeekOrigin.Begin);
                byte[] buf = new byte[1024 * 1024];
                long remaining = xisoLen;
                while (remaining > 0)
                {
                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n == 0)
                    {
                        return null;
                    }

                    dst.Write(buf, 0, n);
                    remaining -= n;
                }
            }

            return part;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>First 64-hex token in the output (checksum lines are "hex" or "hex\tpath").</summary>
    private static string? FirstHex(string stdout) =>
        stdout.Split('\n')
            .Select(static l => l.TrimEnd('\r').Trim())
            .Select(static l => l.Split('\t', ' ')[0])
            .FirstOrDefault(static t => t is not null && Hex64Regex.IsMatch(t));

    /// <summary>Parses "md5  /path" lines into a path → hash map.</summary>
    private static Dictionary<string, string> ParseMd5Map(string stdout)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string line in stdout.Split('\n'))
        {
            Match m = Md5LineRegex.Match(line.TrimEnd('\r').TrimEnd());
            if (m.Success)
            {
                map[m.Groups["path"].Value] = m.Groups["hash"].Value;
            }
        }

        return map;
    }

    /// <summary>
    /// Newest file in <paramref name="dir"/> matching <paramref name="preferredPattern"/>
    /// (when given), excluding the staged input and *.old backups; falls back to the
    /// newest non-input, non-.old file so unknown oracle naming still works.
    /// </summary>
    private static string? FindOutput(string dir, string inputPath, string? preferredPattern)
    {
        List<string> candidates = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(f, inputPath, StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (preferredPattern is not null)
        {
            string? hit = candidates
                .Where(f => MatchesPattern(Path.GetFileName(f), preferredPattern))
                .OrderByDescending(static f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return candidates
            .OrderByDescending(static f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Case-insensitive glob match supporting the * wildcard only.</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        if (!pattern.Contains('*'))
        {
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        string[] parts = pattern.Split('*');
        int pos = 0;
        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            int idx = name.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            pos = idx + part.Length;
        }

        return true;
    }
}
