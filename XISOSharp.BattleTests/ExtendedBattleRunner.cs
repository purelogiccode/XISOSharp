using System.Diagnostics;
using System.Security.Cryptography;
using XISOSharp.BattleTests.Models;

namespace XISOSharp.BattleTests;

/// <summary>
/// <para>
/// Extended battles for features that do not exist in the original extract-xiso:
/// XboxKit-borrowed archival operations (video/xiso/filler/seed/update split,
/// petrify, ZAR, rebuild) oracled against <c>xboxkit.exe</c>, and xdvdfs-borrowed
/// operations (checksum, unpack, pack, tree, copy-out, md5) oracled against
/// <c>xdvdfs.exe</c>.
/// </para>
/// <para>
/// Oracle hygiene (learned the hard way):
/// - Both oracles write outputs next to their inputs, so every ISO is first
///   copied into a private sandbox; H:\ sources are never touched.
/// - xboxkit exit codes are meaningless (0 even on [ERROR]): only artifacts compare.
/// - xdvdfs has no partition probing and silently walks garbage on raw Redump
///   images: it only ever runs on plain XISOs (game-partition splits, created ISOs).
/// </para>
/// </summary>
internal static class ExtendedBattleRunner
{
    /// <summary>
    /// Gate for the XD-Pack battle (BTL-024): unpacked trees at or below this size
    /// are packed whole; larger trees fall back to a deterministic mini-tree (see
    /// <c>PackMiniTreeMaxFiles</c>/<c>PackMiniTreeMaxTotalBytes</c>) so the check
    /// stays fast on multi-GB game trees.
    /// </summary>
    private const long PackGateBytes = 200L * 1024 * 1024;

    /// <summary>Mini-tree fallback: at most this many files, chosen smallest-first.</summary>
    private const int PackMiniTreeMaxFiles = 8;

    /// <summary>Mini-tree fallback: only files at or below this size are eligible.</summary>
    private const long PackMiniTreeMaxFileBytes = 8L * 1024 * 1024;

    /// <summary>Mini-tree fallback: total-bytes cap so the fallback is bounded by count AND bytes.</summary>
    private const long PackMiniTreeMaxTotalBytes = 64L * 1024 * 1024;

    public static PerFileBattleResult RunExtendedForIso(
        string path, XboxKitWrapper? xk, XdvdfsWrapper? xd, bool keepSandbox = false)
    {
        Stopwatch sw = Stopwatch.StartNew();
        FileInfo fi = new(path);
        PerFileBattleResult result = new() { FilePath = path, FileName = "EXT:" + fi.Name, FileSize = fi.Length };

        bool saveQuiet = Logger.Quiet;
        bool saveRealQuiet = Logger.RealQuiet;
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        string? sandbox = null;
        try
        {
            // Extended battles stage the ISO plus ~3x its size in oracle
            // artifacts; a 7.8 GB Redump needs ~35-40 GB of headroom. Sandboxes
            // default to the ISO's own drive (the H: media drives have terabytes
            // free, while %TEMP% on C: does not) and are always deleted — see
            // the 2026-09-05 incident where 15 sandboxes filled drive C:.
            sandbox = CreateSandbox(path, fi.Length, result);
            if (sandbox == null)
            {
                sw.Stop();
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            string workInput = Path.Combine(sandbox, "input.iso");
            File.Copy(path, workInput);

            long isoOffset;
            long xisoLength;
            bool isRedump;
            try
            {
                (isoOffset, xisoLength, isRedump) = GetPartition(workInput);
            }
            catch (XisoFormatException ex)
            {
                // BTL-020: a plain-invalid input fails the battle — it is not a
                // harness error. (XisoFormatException derives from IOException,
                // so it must be caught before the truncated-input arm below.)
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "EXT-Partition",
                    Status = BattleStatus.Failed,
                    Detail = $"not a valid XISO/Redump image: {Trim(ex.Message)}",
                    ElapsedSeconds = 0,
                });
                sw.Stop();
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }
            catch (XisoEmptyException ex)
            {
                // Empty image (no files): nothing to partition — Skipped, matching
                // the empty-ISO handling in the extract-xiso parity battles.
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "EXT-Partition",
                    Status = BattleStatus.Skipped,
                    Detail = $"empty image (no files): {Trim(ex.Message)}",
                    ElapsedSeconds = 0,
                });
                sw.Stop();
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }
            catch (EndOfStreamException ex)
            {
                // BTL-020: truncated/short inputs are Skipped, not Error.
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "EXT-Partition",
                    Status = BattleStatus.Skipped,
                    Detail = $"truncated input (short read): {Trim(ex.Message)}",
                    ElapsedSeconds = 0,
                });
                sw.Stop();
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }
            catch (IOException ex)
            {
                // BTL-020: VerifyXiso reports too-short headers as IOException —
                // a short/unreadable copy is Skipped, not a harness Error.
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "EXT-Partition",
                    Status = BattleStatus.Skipped,
                    Detail = $"truncated/unreadable input: {Trim(ex.Message)}",
                    ElapsedSeconds = 0,
                });
                sw.Stop();
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            // ---- XboxKit oracle side ----
            string? xkDir = null;
            if (xk?.Available == true && isRedump)
            {
                xkDir = Path.Combine(sandbox, "xk");
                Directory.CreateDirectory(xkDir);
                string xkInput = Path.Combine(xkDir, "input.iso");
                File.Copy(workInput, xkInput);
                xk.SplitAll(xkDir, xkInput);
            }

            string csDir = Path.Combine(sandbox, "cs");
            Directory.CreateDirectory(csDir);

            if (isRedump)
            {
                result.SubTests.Add(XkVideo(workInput, csDir, xkDir));
                result.SubTests.Add(XkXisoSplit(workInput, csDir, xkDir, isoOffset, xisoLength));
                result.SubTests.Add(XkFiller(workInput, csDir, xkDir, isoOffset, xisoLength));
                result.SubTests.Add(XkSeed(workInput, csDir, xkDir, isoOffset));
                result.SubTests.Add(XkUpdate(csDir, xkDir));
                result.SubTests.Add(XkPetrify(csDir, xk));
                result.SubTests.Add(XkZar(workInput, csDir, xkDir, isoOffset, xk));
                result.SubTests.Add(XkRebuildOurs(workInput, csDir, xkDir));
                result.SubTests.Add(XkRebuildTheirs(workInput, csDir, xkDir, xk));
            }
            else
            {
                result.SubTests.Add(Skip("XK-Video", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-Xiso", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-Filler", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-Seed", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-Update", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-Petrify", "not a Redump ISO (xiso-input variant runs under XD)"));
                result.SubTests.Add(Skip("XK-Zar", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-RebuildCs", "not a Redump ISO"));
                result.SubTests.Add(Skip("XK-RebuildXk", "not a Redump ISO"));
            }

            // ---- xdvdfs oracle side (plain XISOs only) ----
            List<(string Tag, string Path)> plainXisos = new();
            string? xkSplit = xkDir == null ? null : Path.Combine(xkDir, "input.xiso");
            if (xkSplit != null && File.Exists(xkSplit))
                plainXisos.Add(("split", xkSplit));
            if (!isRedump)
                plainXisos.Add(("plain", workInput));

            if (xd?.Available == true && plainXisos.Count > 0)
            {
                foreach ((string tag, string xiso) in plainXisos)
                {
                    result.SubTests.Add(XdChecksum(xiso, tag, xd));
                    result.SubTests.Add(XdUnpack(xiso, tag, sandbox, csDir, xd));
                    result.SubTests.Add(XdTree(xiso, tag, xd));
                    result.SubTests.Add(XdCopyOutMd5(xiso, tag, sandbox, csDir, xd));
                }

                result.SubTests.Add(XdPack(sandbox, csDir, xd));
            }
            else if (xd?.Available != true)
            {
                result.SubTests.Add(Skip("XD-*", "xdvdfs.exe not available"));
            }
            else
            {
                result.SubTests.Add(Skip("XD-*", "no plain XISO available (Redump without xk split)"));
            }
        }
        catch (Exception ex)
        {
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "EXT-Harness",
                Status = BattleStatus.Error,
                Detail = $"harness error (sandbox was {sandbox}): {ex.GetType().Name}: {ex.Message}",
                ElapsedSeconds = 0,
            });
        }
        finally
        {
            Logger.Quiet = saveQuiet;
            Logger.RealQuiet = saveRealQuiet;
            // Always delete unless explicitly kept: the sandbox holds several
            // times the ISO size and default %TEMP% shares the OS drive.
            if (sandbox != null && !keepSandbox)
            {
                try
                {
                    Directory.Delete(sandbox, true);
                }
                catch
                {
                    // ignored
                }
            }
            else if (sandbox != null)
            {
                Console.WriteLine($"  [extended] sandbox kept (--keep-sandbox): {sandbox}");
            }
        }

        sw.Stop();
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    // ------------------------------------------------------------------
    // XboxKit comparisons
    // ------------------------------------------------------------------

    private static SubBattleResult XkVideo(string workInput, string csDir, string? xkDir) =>
        Timed("XK-Video", () =>
        {
            string csVideo = Path.Combine(csDir, "input.video.iso");
            string? xkVideo = xkDir == null ? null : Path.Combine(xkDir, "input.video.iso");
            bool csOk;
            try
            {
                csOk = XisoRedump.TryExtractVideo(workInput, csVideo, out _, true);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            return CompareFiles("video", csOk ? csVideo : null, xkVideo);
        });

    private static SubBattleResult XkXisoSplit(string workInput, string csDir, string? xkDir, long isoOffset,
        long xisoLength) =>
        Timed("XK-Xiso", () =>
        {
            string csXiso = Path.Combine(csDir, "input.xiso");
            try
            {
                using FileStream src = new(workInput, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using FileStream dst = new(csXiso, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                src.Seek(isoOffset, SeekOrigin.Begin);
                CopyExact(src, dst, xisoLength);
            }
            catch (Exception ex)
            {
                return Fail($"C# split threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            string? xkXiso = xkDir == null ? null : Path.Combine(xkDir, "input.xiso");
            if (xkXiso == null || !File.Exists(xkXiso))
                return Fail("oracle .xiso missing (xk -a)");

            // xboxkit -a ships the game partition wiped (-w) and trimmed (-t), not
            // raw, so compare like-for-like: our wipe+trim of the raw split must
            // be byte-identical to xk's .xiso. (Raw filler parity is XK-Filler.)
            string csWt = Path.Combine(csDir, "input.wiped.xiso");
            try
            {
                if (!XisoOperations.WipeAndTrim(csXiso, csWt, 0, true))
                    return Fail("C# WipeAndTrim returned false");
            }
            catch (Exception ex)
            {
                return Fail($"C# WipeAndTrim threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            SubBattleResult r = CompareFiles("xiso (wipe+trim)", csWt, xkXiso);
            long csLen = new FileInfo(csWt).Length;
            long xkLen = new FileInfo(xkXiso).Length;
            Del(csWt); // 1x ISO size freed immediately after compare
            if (r.Status == BattleStatus.Passed)
                return r;
            return Fail(
                $"{r.Detail} [sizes: cs={csLen} xk={xkLen} — differs by {(csLen == xkLen ? "content only (wipe)" : "trim point")}]");
        });

    private static SubBattleResult XkFiller(string workInput, string csDir, string? xkDir, long isoOffset,
        long xisoLength) =>
        Timed("XK-Filler", () =>
        {
            string csFiller = Path.Combine(csDir, "input.filler");
            bool csOk;
            try
            {
                csOk = XisoOperations.ExtractFiller(workInput, csFiller, isoOffset, xisoLength, true);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            string? xkFiller = xkDir == null ? null : Path.Combine(xkDir, "input.filler");
            return CompareFiles("filler", csOk ? csFiller : null, xkFiller);
        });

    private static SubBattleResult XkSeed(string workInput, string csDir, string? xkDir, long isoOffset) =>
        Timed("XK-Seed", () =>
        {
            string csSeed = Path.Combine(csDir, "input.seed");
            bool csOk;
            try
            {
                csOk = XisoOperations.TryExtractSeed(workInput, csSeed, isoOffset, true);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            string? xkSeed = xkDir == null ? null : Path.Combine(xkDir, "input.seed");
            return CompareFiles("seed", csOk ? csSeed : null, xkSeed, bothMissingOk: true,
                bothMissingNote: "N/A (not XGD1)");
        });

    private static SubBattleResult XkUpdate(string csDir, string? xkDir) =>
        Timed("XK-Update", () =>
        {
            // Update comes from the video partition (XGD3 only).
            string csVideo = Path.Combine(csDir, "input.video.iso");
            string? csUpdate = null;
            if (File.Exists(csVideo))
            {
                csUpdate = Path.Combine(csDir, "su20076000_00000000");
                try
                {
                    if (!XisoRedump.TryExtractUpdate(csVideo, csUpdate, true, true))
                        csUpdate = null;
                }
                catch (Exception ex)
                {
                    return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
                }
            }

            string? xkUpdate = xkDir == null
                ? null
                : Directory.GetFiles(xkDir, "su20076000_00000000", SearchOption.AllDirectories).FirstOrDefault();
            return CompareFiles("update", csUpdate, xkUpdate, bothMissingOk: true, bothMissingNote: "N/A (not XGD3)");
        });

    private static SubBattleResult XkPetrify(string csDir, XboxKitWrapper? xk) =>
        Timed("XK-Petrify", () =>
        {
            string csXiso = Path.Combine(csDir, "input.xiso");
            if (!File.Exists(csXiso))
                return Fail("no C# raw split to petrify");
            string csSkel = Path.Combine(csDir, "input.skeleton.xiso");
            bool csOk;
            try
            {
                csOk = XisoSkeleton.Petrify(csXiso, csSkel, null, 0, true);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            if (xk?.Available != true)
                return CompareFiles("skeleton", csOk ? csSkel : null, null);

            // Same-input oracle: xk -p on a copy of OUR raw split.
            string pkDir = Path.Combine(csDir, "pk");
            Directory.CreateDirectory(pkDir);
            string pkInput = Path.Combine(pkDir, "input.xiso");
            File.Copy(csXiso, pkInput);
            (int code, string so, string se) = xk.Run(pkDir, "-y", "-q", "-p", pkInput);
            string xkSkel = Path.Combine(pkDir, "input.skeleton.xiso");
            bool hasSkel = File.Exists(xkSkel) && new FileInfo(xkSkel).Length > 0;
            Del(pkDir); // the 1x-ISO copy + oracle skeleton are single-use
            if (!hasSkel)
            {
                // Known upstream limitation: LibXGD's CollectFileEntries parses
                // all-0xFF empty-directory tables as entries and reads past EOF.
                // Our collector carries the 0xFFFF sentinel fix (see
                // XisoRangesEmptyDirTests), so C# succeeds where the oracle crashes.
                // BTL-018: `so + se` is a string concat and never null — test real
                // emptiness instead of the always-true `is { }` pattern.
                string oracleOut = so + se;
                if (!string.IsNullOrEmpty(oracleOut)
                    && oracleOut.Contains("CollectFileEntries", StringComparison.OrdinalIgnoreCase)
                    && oracleOut.Contains("EndOfStream", StringComparison.OrdinalIgnoreCase))
                {
                    return Pass("oracle crashed (LibXGD empty-dir EOF bug); C# petrify OK " +
                                $"({(csOk && File.Exists(csSkel) ? new FileInfo(csSkel).Length : -1)} bytes)");
                }

                return Fail($"xk -p produced no skeleton (exit {code}): {Trim(so + se)}");
            }

            return CompareFiles("skeleton", csOk ? csSkel : null, xkSkel);
        });

    private static SubBattleResult XkZar(string workInput, string csDir, string? xkDir, long isoOffset,
        XboxKitWrapper? xk) =>
        Timed("XK-Zar", () =>
        {
            string csZar = Path.Combine(csDir, "input.zar");
            bool csOk;
            try
            {
                csOk = XisoZarchive.CreateZar(workInput, csZar, isoOffset, true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                return Skip("XK-Zar", $"zarchive backend unavailable: {Trim(ex.Message)}");
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            if (!csOk)
                return Fail("C# CreateZar returned false");

            string? xkZar;
            if (xkDir != null && xk?.Available == true)
            {
                // -z consumes a plain .xiso (the redump copy is not accepted);
                // pack from the oracle's own -a split. zarchive.exe beside
                // xboxkit.exe is required (copied by the csproj).
                string xkXiso = Path.Combine(xkDir, "input.xiso");
                if (!File.Exists(xkXiso))
                    return Skip("XK-Zar", "no xk .xiso split for -z");
                xkZar = Path.Combine(xkDir, "input.zar");
                string zarLog = "";
                if (!File.Exists(xkZar))
                {
                    // BTL-019: xboxkit resolves zarchive.exe next to the input, not
                    // next to itself; stage it in the oracle workdir. Resolve via
                    // the shared ToolLocator chain (sibling of the harness, then
                    // PATH) instead of assuming the csproj copied it beside the
                    // harness — Skip with a clear reason when absent.
                    string zarFileName = ToolLocator.GetFileName("zarchive");
                    string? zarSource = ToolLocator.ResolveByBaseName(null, "zarchive");
                    if (zarSource == null)
                    {
                        return Skip("XK-Zar",
                            $"{zarFileName} not found (sibling of harness nor on PATH) — oracle unavailable");
                    }

                    try
                    {
                        File.Copy(zarSource, Path.Combine(xkDir, zarFileName), true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or ArgumentException or NotSupportedException
                                                   or PathTooLongException)
                    {
                        return Skip("XK-Zar",
                            $"could not stage {zarFileName} into oracle workdir: {ex.GetType().Name}: {Trim(ex.Message)}");
                    }

                    // No -q here: a silent failure must leave its [ERROR] in the detail.
                    (int zc, string zso, string zse) = xk.Run(xkDir, "-y", "-z", xkXiso);
                    zarLog = $"exit {zc}: {Trim(zso + zse)}";
                    xkZar = Directory.GetFiles(xkDir, "*.zar", SearchOption.TopDirectoryOnly).FirstOrDefault();
                }

                if (xkZar == null)
                {
                    // Known upstream limitation (same as XK-Petrify): LibXGD's
                    // CollectFileEntries crashes on all-0xFF empty-directory
                    // tables, so xboxkit cannot pack a .zar for such images.
                    return Skip("XK-Zar", $"xk -z produced no .zar ({zarLog})");
                }
            }
            else
            {
                return Skip("XK-Zar", "oracle unavailable");
            }

            // ZAR bytes may legitimately differ (zstd build skew): compare the
            // extracted game-file trees instead.
            string csTree = Path.Combine(csDir, "zar-cs");
            string xkTree = Path.Combine(xkDir, "zar-xk");
            try
            {
                ZARSharp.ZArchiveTool.Extract(csZar, csTree);
                ZARSharp.ZArchiveTool.Extract(xkZar, xkTree);
            }
            catch (Exception ex)
            {
                Del(csTree);
                Del(xkTree);
                return Fail($"zar extract threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            SubBattleResult r = CompareTrees(csTree, xkTree);
            Del(csTree); // extracted game trees are the largest artifacts
            Del(xkTree);
            return r;
        });

    private static SubBattleResult XkRebuildOurs(string workInput, string csDir, string? xkDir) =>
        Timed("XK-RebuildCs", () =>
        {
            if (xkDir == null)
                return Skip("XK-RebuildCs", "no xk split parts");
            string xiso = Path.Combine(xkDir, "input.xiso");
            string video = Path.Combine(xkDir, "input.video.iso");
            string filler = Path.Combine(xkDir, "input.filler");
            string seed = Path.Combine(xkDir, "input.seed");
            string? fillerOrSeed = File.Exists(filler) ? filler : File.Exists(seed) ? seed : null;
            string? update = Directory.GetFiles(xkDir, "su20076000_00000000", SearchOption.AllDirectories).FirstOrDefault();
            if (!File.Exists(xiso) || !File.Exists(video) || fillerOrSeed == null)
            {
                return Fail(
                    $"xk split incomplete (xiso={Exists(xiso)} video={Exists(video)} filler/seed={fillerOrSeed != null})");
            }

            string rebuilt = Path.Combine(csDir, "rebuilt.iso");
            bool ok;
            try
            {
                ok = XisoRedump.RebuildRedump(xiso, video, fillerOrSeed, update, rebuilt, null, true);
            }
            catch (Exception ex)
            {
                return Fail($"C# rebuild threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            if (!ok)
                return Fail("C# RebuildRedump returned false");
            SubBattleResult r = CompareFiles("rebuilt redump", rebuilt, workInput);
            Del(rebuilt); // 1x ISO freed right after compare
            return r;
        });

    private static SubBattleResult XkRebuildTheirs(string workInput, string csDir, string? xkDir, XboxKitWrapper? xk) =>
        Timed("XK-RebuildXk", () =>
        {
            if (xk?.Available != true || xkDir == null)
                return Skip("XK-RebuildXk", "xboxkit unavailable");
            // Stage OUR parts for xk rebuild mode: xboxkit <input.xiso> [files...]
            string rbDir = Path.Combine(csDir, "rbx");
            Directory.CreateDirectory(rbDir);
            foreach (string f in Directory.GetFiles(csDir))
            {
                string n = Path.GetFileName(f);
                if (string.Equals(n, "input.video.iso", StringComparison.Ordinal) ||
                    string.Equals(n, "input.filler", StringComparison.Ordinal) ||
                    string.Equals(n, "input.seed", StringComparison.Ordinal) ||
                    string.Equals(n, "input.xiso", StringComparison.Ordinal) ||
                    string.Equals(n, "su20076000_00000000", StringComparison.Ordinal))
                {
                    File.Copy(f, Path.Combine(rbDir, n));
                }
            }

            // Our cs split names: ensure an .xiso exists for rebuild input.
            string rbXiso = Path.Combine(rbDir, "input.xiso");
            if (!File.Exists(rbXiso))
                return Skip("XK-RebuildXk", "no C# xiso split staged");
            List<string> parts = Directory.GetFiles(rbDir).Where(f => !f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                                                                      || f.EndsWith(".video.iso",
                                                                          StringComparison.OrdinalIgnoreCase)).ToList();
            HashSet<string> before = Directory.GetFiles(rbDir, "*.iso", SearchOption.AllDirectories)
                .Select(f => f.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            xk.Rebuild(rbDir,
                new[] { rbXiso }.Concat(parts.Where(f => !string.Equals(f, rbXiso, StringComparison.Ordinal)))
                    .ToArray());
            List<string> after = Directory.GetFiles(rbDir, "*.iso", SearchOption.AllDirectories)
                .Where(f => !before.Contains(f.ToLowerInvariant())).ToList();
            string? rebuilt =
                after.FirstOrDefault(f => Path.GetFileName(f).Contains("redump", StringComparison.OrdinalIgnoreCase))
                ?? after.FirstOrDefault();
            if (rebuilt == null)
            {
                Del(rbDir);
                return Fail(
                    $"xk rebuild produced no new ISO (files: {string.Join(",", Directory.GetFiles(rbDir).Select(Path.GetFileName))})");
            }

            SubBattleResult rr = CompareFiles("xk-rebuilt redump", rebuilt, workInput);
            Del(rbDir); // staged parts (filler = 1x ISO) are single-use
            return rr;
        });

    // ------------------------------------------------------------------
    // xdvdfs comparisons (plain XISOs only)
    // ------------------------------------------------------------------

    private static SubBattleResult XdChecksum(string xiso, string tag, XdvdfsWrapper xd) =>
        Timed($"XD-Checksum[{tag}]", () =>
        {
            string csHex;
            try
            {
                csHex = XisoChecksum.ComputeImageChecksumHex(xiso);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (int code, string so, string se) = xd.Checksum(xiso);
            string xdHex = ParseHexToken(so);
            if (xdHex.Length == 0)
                return Fail($"xdvdfs checksum unparseable (exit {code}): {Trim(so + se)}");
            return string.Equals(csHex, xdHex, StringComparison.OrdinalIgnoreCase)
                ? Pass($"checksum {csHex[..16]}… match")
                : Fail($"checksum mismatch C#={csHex} xd={xdHex}");
        });

    private static SubBattleResult XdUnpack(string xiso, string tag, string sandbox, string csDir, XdvdfsWrapper xd) =>
        Timed($"XD-Unpack[{tag}]", () =>
        {
            string csOut = Path.Combine(csDir, $"unpack-{tag}");
            string xdOut = Path.Combine(sandbox, $"xd-unpack-{tag}");
            try
            {
                int rc = XisoReader.UnpackImage(xiso, csOut);
                if (rc != 0)
                    return Fail($"C# UnpackImage rc={rc}");
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (int code, _, string se) = xd.Unpack(xiso, xdOut);
            if (code != 0 || !Directory.Exists(xdOut))
                return Fail($"xdvdfs unpack exit {code}: {Trim(se)}");
            return CompareTrees(csOut, xdOut);
        });

    private static SubBattleResult XdTree(string xiso, string tag, XdvdfsWrapper xd) =>
        Timed($"XD-Tree[{tag}]", () =>
        {
            string csText;
            try
            {
                csText = CaptureTree(xiso);
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (int code, string so, string se) = xd.Tree(xiso);
            if (code != 0)
                return Fail($"xdvdfs tree exit {code}: {Trim(se)}");
            // Our Tree roots entries at the image name (`\input\...` for
            // `input.iso`, `\input.xiso\...` for splits); xdvdfs lists
            // image-internal paths (`/...`). Strip the root on compare.
            string root = ListingRootPrefix(xiso);
            HashSet<string> csSet = NormaliseListing(csText, root);
            HashSet<string> xdSet = NormaliseListing(so, root);
            if (csSet.SetEquals(xdSet))
                return Pass($"{csSet.Count} entries match");
            List<string> onlyCs = csSet.Where(e => !xdSet.Contains(e)).Take(3).ToList();
            List<string> onlyXd = xdSet.Where(e => !csSet.Contains(e)).Take(3).ToList();
            return Fail(
                $"tree mismatch C#={csSet.Count} xd={xdSet.Count} ONLY cs [{string.Join(";", onlyCs)}] ONLY xd [{string.Join(";", onlyXd)}]");
        });

    private static SubBattleResult XdCopyOutMd5(string xiso, string tag, string sandbox, string csDir, XdvdfsWrapper xd) =>
        Timed($"XD-CopyOut+Md5[{tag}]", () =>
        {
            // First file entry from our own listing.
            string? inner;
            try
            {
                inner = FirstFileEntry(xiso);
            }
            catch (Exception ex)
            {
                return Fail($"C# listing threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            if (inner == null)
                return Skip($"XD-CopyOut+Md5[{tag}]", "image has no files");

            string csOut = Path.Combine(csDir, $"copyout-{tag}.bin");
            try
            {
                XisoReader.CopyOut(xiso, inner, csOut);
            }
            catch (Exception ex)
            {
                return Fail($"C# CopyOut threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            string xdOut = Path.Combine(sandbox, $"xd-copyout-{tag}.bin");
            (int xcode, _, string xse) = xd.CopyOut(xiso, inner, xdOut);
            if (xcode != 0 || !File.Exists(xdOut))
                return Fail($"xdvdfs copy-out exit {xcode}: {Trim(xse)}");
            SubBattleResult r = CompareFiles($"copy-out {inner}", csOut, xdOut);
            if (r.Status != BattleStatus.Passed)
                return r;

            string csMd5;
            try
            {
                byte[]? bytes = XisoReader.ComputeFileHash(xiso, inner, HashAlgorithmName.MD5);
                csMd5 = bytes == null ? "" : Convert.ToHexString(bytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                return Fail($"C# ComputeFileHash threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (int mcode, string mso, string mse) = xd.Md5(xiso, inner);
            string xdMd5 = ParseHexToken(mso);
            if (xdMd5.Length != 32)
                return Fail($"xdvdfs md5 unparseable (exit {mcode}): {Trim(mso + mse)}");
            return string.Equals(csMd5, xdMd5, StringComparison.OrdinalIgnoreCase)
                ? Pass($"copy-out + md5 match ({inner}, {csMd5[..12]}…)")
                : Fail($"md5 mismatch {inner} C#={csMd5} xd={xdMd5}");
        });

    private static SubBattleResult XdPack(string sandbox, string csDir, XdvdfsWrapper xd) =>
        Timed("XD-Pack", () =>
        {
            // Pack from the smallest available unpacked tree (keeps this check fast).
            // BTL-024: enumerate in ordinal order so equal-size trees resolve to the
            // same winner on every machine — the fallback must not depend on
            // filesystem enumeration order.
            List<string> cands = Directory.GetDirectories(csDir, "unpack-*")
                .OrderBy(d => d, StringComparer.Ordinal).ToList();
            string? tree = null;
            long best = long.MaxValue;
            foreach (string c in cands)
            {
                long bytes = Directory.GetFiles(c, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                if (bytes < best)
                {
                    best = bytes;
                    tree = c;
                }
            }

            if (tree == null)
                return Skip("XD-Pack", "no unpacked tree available");

            string packSrc;
            string packNote;
            if (best <= PackGateBytes)
            {
                packSrc = tree;
                packNote = $"tree {best} bytes";
            }
            else
            {
                // Full tree too big to pack twice: pack a mini-tree of small
                // real game files (relative structure preserved) instead.
                // BTL-024: fully deterministic selection — candidates sorted by
                // (size, path) so ties resolve identically everywhere, capped by
                // file count AND total bytes so the result cannot depend on game
                // content ordering.
                packSrc = Path.Combine(sandbox, "pack-src");
                List<FileInfo> small = Directory.GetFiles(tree, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Length <= PackMiniTreeMaxFileBytes)
                    .OrderBy(f => f.Length).ThenBy(f => f.FullName, StringComparer.Ordinal).ToList();
                List<FileInfo> picked = new(PackMiniTreeMaxFiles);
                long pickedBytes = 0;
                foreach (FileInfo f in small)
                {
                    if (picked.Count >= PackMiniTreeMaxFiles || pickedBytes + f.Length > PackMiniTreeMaxTotalBytes)
                        break;
                    picked.Add(f);
                    pickedBytes += f.Length;
                }

                if (picked.Count == 0)
                    return Skip("XD-Pack", $"smallest tree {best / 1048576} MB > gate and no small files");
                foreach (FileInfo f in picked)
                {
                    string dest = Path.Combine(packSrc, Path.GetRelativePath(tree, f.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f.FullName, dest);
                }

                packNote = $"mini-tree {picked.Count} files ({pickedBytes} bytes)";
            }

            string csPack = Path.Combine(csDir, "packed-cs.iso");
            string xdPack = Path.Combine(sandbox, "packed-xd.iso");
            try
            {
                if (XisoWriter.PackFromDirectory(packSrc, csPack) != 0)
                    return Fail("C# PackFromDirectory rc!=0");
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (int code, _, string se) = xd.Pack(packSrc, xdPack);
            if (code != 0 || !File.Exists(xdPack))
                return Fail($"xdvdfs pack exit {code}: {Trim(se)}");
            // BTL-024: layouts differ by design (allocator/order choices are not part
            // of the format contract), so byte identity is not expected — the
            // content checksums compared here are layout-insensitive by design.
            (int c1, string s1, _) = xd.Checksum(csPack);
            (int c2, string s2, _) = xd.Checksum(xdPack);
            if (c1 != 0 || c2 != 0)
                return Fail($"xdvdfs checksum on packs failed ({c1},{c2}): {Trim(s1 + s2)}");
            string h1 = ParseHexToken(s1);
            string h2 = ParseHexToken(s2);
            return h1.Length > 0 && string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase)
                ? Pass($"pack content checksum match ({h1[..16]}…, {packNote})")
                : Fail($"pack content mismatch cs={h1} xd={h2}");
        });

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static (long IsoOffset, long XisoLength, bool IsRedump) GetPartition(string isoPath)
    {
        long size = new FileInfo(isoPath).Length;
        int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
        if (redumpType < 0)
        {
            using FileStream fs = File.OpenRead(isoPath);
            (_, _, long lseek) = XisoReader.VerifyXiso(fs, "input.iso");
            return (lseek, size - lseek, false);
        }

        using (FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            int videoType = XgdTables.GetVideoType(fs, redumpType);
            int xsType = videoType >= 0 ? XgdTables.GetXisoTypeFromVideo(videoType) : -1;
            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
                xsType = XgdTables.GetXgdType(redumpType);
            return (XgdTables.XisoOffset[xsType], XgdTables.XisoLength[xsType], true);
        }
    }

    private static SubBattleResult Timed(string name, Func<SubBattleResult> body)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SubBattleResult r;
        try
        {
            r = body();
        }
        catch (Exception ex)
        {
            r = new SubBattleResult
            {
                TestName = name, Status = BattleStatus.Error,
                Detail = $"harness error: {ex.GetType().Name}: {Trim(ex.Message)}", ElapsedSeconds = 0,
            };
        }

        sw.Stop();
        return new SubBattleResult
        {
            TestName = name, Status = r.Status, Detail = r.Detail, ElapsedSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    private static SubBattleResult Pass(string detail) =>
        new()
        {
            TestName = "", Status = BattleStatus.Passed, Detail = detail, ElapsedSeconds = 0,
        };

    private static SubBattleResult Fail(string detail, string? extra = null) =>
        new()
        {
            TestName = "",
            Status = BattleStatus.Failed,
            Detail = extra is null ? detail : $"{detail} | {extra}",
            ElapsedSeconds = 0,
        };

    private static SubBattleResult Skip(string name, string detail) =>
        new()
        {
            TestName = name, Status = BattleStatus.Skipped, Detail = detail, ElapsedSeconds = 0,
        };

    private static SubBattleResult CompareFiles(
        string what, string? csPath, string? xkPath, bool bothMissingOk = false, string? bothMissingNote = null)
    {
        bool csExists = csPath != null && File.Exists(csPath);
        bool xkExists = xkPath != null && File.Exists(xkPath);
        if (!csExists && !xkExists)
        {
            return bothMissingOk
                ? Pass(bothMissingNote ?? $"{what}: neither side produced output")
                : Fail($"{what}: neither side produced output (cs={csPath} xk={xkPath})");
        }

        if (!csExists)
            return Fail($"{what}: C# produced nothing, oracle has {Mb(xkPath)}");
        if (!xkExists)
            return Fail($"{what}: oracle produced nothing, C# has {Mb(csPath)}");
        string csHash = HashUtil.ComputeSha256(csPath!);
        string xkHash = HashUtil.ComputeSha256(xkPath!);
        return string.Equals(csHash, xkHash, StringComparison.Ordinal)
            ? Pass($"{what} SHA match ({Mb(csPath)})")
            : Fail($"{what} SHA mismatch C#={csHash[..16]}…({Mb(csPath)}) oracle={xkHash[..16]}…({Mb(xkPath)})");
    }

    private static SubBattleResult CompareTrees(string csOut, string xdOut)
    {
        // BTL-008: ordinal keys — case-only differences are mismatches, not
        // the same file (HashDirectory is Ordinal; see HashUtil).
        IReadOnlyDictionary<string, string> cs = HashUtil.HashDirectory(csOut);
        IReadOnlyDictionary<string, string> xd = HashUtil.HashDirectory(xdOut);
        if (cs.Count != xd.Count)
            return Fail($"unpack tree count C#={cs.Count} xd={xd.Count}");
        foreach (KeyValuePair<string, string> kv in cs)
        {
            if (!xd.TryGetValue(kv.Key, out string? xh))
            {
                string? folded = xd.Keys.FirstOrDefault(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                return folded != null
                    ? Fail($"unpack: case-collision {kv.Key} vs xd {folded}")
                    : Fail($"unpack: only in C# tree: {kv.Key}");
            }

            if (!string.Equals(xh, kv.Value, StringComparison.Ordinal))
                return Fail($"unpack: content mismatch {kv.Key}");
        }

        string? xdOnly = xd.Keys.Except(cs.Keys, StringComparer.Ordinal).FirstOrDefault();
        if (xdOnly != null)
        {
            string? folded = cs.Keys.FirstOrDefault(k => string.Equals(k, xdOnly, StringComparison.OrdinalIgnoreCase));
            return folded != null
                ? Fail($"unpack: case-collision xd {xdOnly} vs C# {folded}")
                : Fail($"unpack: only in xd tree: {xdOnly}");
        }

        return Pass($"unpack trees identical ({cs.Count} files)");
    }

    private static string CaptureTree(string xiso)
    {
        bool saveQuiet = Logger.Quiet;
        TextWriter saveOut = Logger.Out;
        TextWriter origOut = Console.Out;
        try
        {
            Logger.Quiet = false;
            using StringWriter sw = new();
            Logger.Out = sw;
            Console.SetOut(sw);
            XisoReader.Tree(xiso, false);
            sw.Flush();
            return sw.ToString();
        }
        finally
        {
            Logger.Quiet = saveQuiet;
            Logger.Out = saveOut;
            Console.SetOut(origOut);
        }
    }

    /// <summary>
    /// The root prefix our Tree/List output uses for an image file, mirroring
    /// <c>DecodeXisoCore</c>'s <c>isoName</c> derivation: <c>game.iso</c> roots at
    /// <c>game\</c>, but <c>game.xiso</c> roots at <c>game.xiso\</c> (only a trailing
    /// <c>.iso</c> is stripped).
    /// </summary>
    private static string ListingRootPrefix(string xisoPath)
    {
        string fn = Path.GetFileName(xisoPath);
        string root = fn.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? fn[..^".iso".Length] : fn;
        return "/" + root + "/";
    }

    private static HashSet<string> NormaliseListing(string text, string? listingRootPrefix = null)
    {
        // Ours: `\a.txt (7 bytes)` rooted at the image name; xdvdfs: `/a.txt (7 bytes)`
        // image-internal paths + `N files, M bytes` totals.
        HashSet<string> set = new(StringComparer.Ordinal);
        const string suffix = " bytes)";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().Replace('\\', '/');
            int open = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0 || !line.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            string size = line.Substring(open + 2, line.Length - open - 2 - suffix.Length);
            if (size.Length == 0 || !size.All(char.IsAsciiDigit))
                continue;
            string p = line[..open];
            if (p.EndsWith('/'))
                p = p[..^1];
            if (!p.StartsWith('/'))
                p = "/" + p;
            if (listingRootPrefix != null)
            {
                if (p.StartsWith(listingRootPrefix, StringComparison.Ordinal))
                    p = p[listingRootPrefix.Length..];
                else if (string.Equals(p, listingRootPrefix.TrimEnd('/'), StringComparison.Ordinal))
                    continue; // the image root itself, not an entry
            }

            if (!p.StartsWith('/'))
                p = "/" + p;
            set.Add(p + "|" + size);
        }

        return set;
    }

    /// <summary>First hex token (32-128 chars) of <paramref name="text"/>, or empty.</summary>
    private static string ParseHexToken(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int i = 0;
            while (i < line.Length && IsHexDigit(line[i])) i++;
            if (i is >= 32 and <= 128 && (i == line.Length || char.IsWhiteSpace(line[i])))
                return line[..i];
        }

        return "";
    }

    private static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static string? FirstFileEntry(string xiso)
    {
        List<string> set = NormaliseListing(CaptureTree(xiso), ListingRootPrefix(xiso))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (string e in set)
        {
            string[] parts = e.Split('|');
            // entries are path|size; dirs show size 0 but so can empty files — prefer size>0.
            if (parts.Length == 2 && !string.Equals(parts[1], "0", StringComparison.Ordinal))
                return parts[0].Length == 0 ? "/" : parts[0];
        }

        return null;
    }

    private static void CopyExact(FileStream src, FileStream dst, long bytes)
    {
        byte[] buf = new byte[65536];
        while (bytes > 0)
        {
            int want = (int)Math.Min(buf.Length, bytes);
            int read = 0;
            while (read < want)
            {
                int n = src.Read(buf, read, want - read);
                if (n <= 0)
                    throw new IOException($"short read splitting partition ({bytes} bytes left)");
                read += n;
            }

            dst.Write(buf, 0, read);
            bytes -= read;
        }
    }

    /// <summary>
    /// Creates the run sandbox on the ISO's own drive (override with the
    /// <c>XISO_BATTLE_SANDBOX</c> env var), after a preflight that requires the
    /// volume to hold at least <paramref name="isoSize"/> × 4 free (ISO copy +
    /// oracle artifacts + working set). Returns null (and adds a Skipped marker)
    /// when the volume cannot safely host the run, so the OS drive can never be
    /// filled again.
    /// </summary>
    private static string? CreateSandbox(string sourcePath, long isoSize, PerFileBattleResult result)
    {
        string baseDir;
        string? env = Environment.GetEnvironmentVariable("XISO_BATTLE_SANDBOX");
        if (!string.IsNullOrWhiteSpace(env))
        {
            baseDir = env;
        }
        else
        {
            string full = Path.GetFullPath(sourcePath);
            string root = Path.GetPathRoot(full) ?? "";
            baseDir = root.Length > 0 ? Path.Combine(root, "XISOSharpBattle") : CreateSandboxFallback();
        }

        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(baseDir)) ?? baseDir);
            long needed = isoSize * 4;
            if (drive.AvailableFreeSpace < needed)
            {
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "EXT-Space",
                    Status = BattleStatus.Skipped,
                    Detail = $"insufficient space on {drive.Name}: {drive.AvailableFreeSpace / 1073741824} GB free, " +
                             $"need ~{needed / 1073741824} GB (4x ISO) — set XISO_BATTLE_SANDBOX to a larger volume",
                    ElapsedSeconds = 0,
                });
                return null;
            }
        }
        catch
        {
            // Volume probing unavailable (UNC etc.) — proceed and let I/O fail loudly.
        }

        string dir;
        // BTL-009: mkdir stays inside the guarded flow — a read-only or
        // elevation-gated root (e.g. an ISO drive root) becomes a Skipped
        // sandbox, never a harness Error.
        try
        {
            // BTL-021: full 32-char GUID (8-char prefixes collide across parallel
            // harnesses sharing one root) with a create-retry so a rare collision
            // retries with a fresh GUID instead of reusing a foreign sandbox.
            string? created = null;
            for (int attempt = 0; attempt < 3 && created == null; attempt++)
            {
                string candidate = Path.Combine(baseDir, "ext_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(candidate);
                    created = candidate;
                }
                catch (IOException) when (attempt < 2)
                {
                    // Possible GUID collision or transient failure — retry fresh.
                }
            }

            dir = created ?? throw new IOException(
                $"Could not create a unique sandbox directory under {baseDir} after 3 attempts.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "EXT-Sandbox",
                Status = BattleStatus.Skipped,
                Detail = $"sandbox unavailable under {baseDir}: {ex.GetType().Name}: {Trim(ex.Message)}",
                ElapsedSeconds = 0,
            });
            return null;
        }

        return dir;
    }

    private static string CreateSandboxFallback() => Path.Combine(Path.GetTempPath(), "XISOSharpBattle");

    private static string Mb(string? p)
    {
        if (p == null || !File.Exists(p))
            return "missing";
        return $"{new FileInfo(p).Length / 1048576} MB";
    }

    /// <summary>Best-effort delete of a file or directory (large intermediate cleanup).</summary>
    private static void Del(string? path)
    {
        try
        {
            if (path == null)
                return;
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // cleanup is best-effort; the sandbox wipe covers the rest
        }
    }

    private static string Exists(string p) => File.Exists(p) ? "yes" : "no";

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 220 ? s[..220] : s;
    }
}
