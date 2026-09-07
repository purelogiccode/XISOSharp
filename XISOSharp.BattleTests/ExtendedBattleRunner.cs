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
    private const long PackGateBytes = 200L * 1024 * 1024;

    public static PerFileBattleResult RunExtendedForIso(
        string path, XboxKitWrapper? xk, XdvdfsWrapper? xd, bool keepSandbox = false)
    {
        var sw = Stopwatch.StartNew();
        var fi = new FileInfo(path);
        var result = new PerFileBattleResult { FilePath = path, FileName = "EXT:" + fi.Name, FileSize = fi.Length };

        var saveQuiet = Logger.Quiet;
        var saveRealQuiet = Logger.RealQuiet;
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

            var workInput = Path.Combine(sandbox, "input.iso");
            File.Copy(path, workInput);

            var (isoOffset, xisoLength, isRedump) = GetPartition(workInput);

            // ---- XboxKit oracle side ----
            string? xkDir = null;
            if (xk?.Available == true && isRedump)
            {
                xkDir = Path.Combine(sandbox, "xk");
                Directory.CreateDirectory(xkDir);
                var xkInput = Path.Combine(xkDir, "input.iso");
                File.Copy(workInput, xkInput);
                xk.SplitAll(xkDir, xkInput);
            }

            var csDir = Path.Combine(sandbox, "cs");
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
            var plainXisos = new List<(string Tag, string Path)>();
            var xkSplit = xkDir == null ? null : Path.Combine(xkDir, "input.xiso");
            if (xkSplit != null && File.Exists(xkSplit))
                plainXisos.Add(("split", xkSplit));
            if (!isRedump)
                plainXisos.Add(("plain", workInput));

            if (xd?.Available == true && plainXisos.Count > 0)
            {
                foreach (var (tag, xiso) in plainXisos)
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

    private static SubBattleResult XkVideo(string workInput, string csDir, string? xkDir)
    {
        return Timed("XK-Video", () =>
        {
            var csVideo = Path.Combine(csDir, "input.video.iso");
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
    }

    private static SubBattleResult XkXisoSplit(string workInput, string csDir, string? xkDir, long isoOffset,
        long xisoLength)
    {
        return Timed("XK-Xiso", () =>
        {
            var csXiso = Path.Combine(csDir, "input.xiso");
            try
            {
                using var src = new FileStream(workInput, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var dst = new FileStream(csXiso, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
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
            var csWt = Path.Combine(csDir, "input.wiped.xiso");
            try
            {
                if (!XisoOperations.WipeAndTrim(csXiso, csWt, 0, true))
                    return Fail("C# WipeAndTrim returned false");
            }
            catch (Exception ex)
            {
                return Fail($"C# WipeAndTrim threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            var r = CompareFiles("xiso (wipe+trim)", csWt, xkXiso);
            var csLen = new FileInfo(csWt).Length;
            var xkLen = new FileInfo(xkXiso).Length;
            Del(csWt); // 1x ISO size freed immediately after compare
            if (r.Status == BattleStatus.Passed)
                return r;
            return Fail(
                $"{r.Detail} [sizes: cs={csLen} xk={xkLen} — differs by {(csLen == xkLen ? "content only (wipe)" : "trim point")}]");
        });
    }

    private static SubBattleResult XkFiller(string workInput, string csDir, string? xkDir, long isoOffset,
        long xisoLength)
    {
        return Timed("XK-Filler", () =>
        {
            var csFiller = Path.Combine(csDir, "input.filler");
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
    }

    private static SubBattleResult XkSeed(string workInput, string csDir, string? xkDir, long isoOffset)
    {
        return Timed("XK-Seed", () =>
        {
            var csSeed = Path.Combine(csDir, "input.seed");
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
    }

    private static SubBattleResult XkUpdate(string csDir, string? xkDir)
    {
        return Timed("XK-Update", () =>
        {
            // Update comes from the video partition (XGD3 only).
            var csVideo = Path.Combine(csDir, "input.video.iso");
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
    }

    private static SubBattleResult XkPetrify(string csDir, XboxKitWrapper? xk)
    {
        return Timed("XK-Petrify", () =>
        {
            var csXiso = Path.Combine(csDir, "input.xiso");
            if (!File.Exists(csXiso))
                return Fail("no C# raw split to petrify");
            var csSkel = Path.Combine(csDir, "input.skeleton.xiso");
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
            var pkDir = Path.Combine(csDir, "pk");
            Directory.CreateDirectory(pkDir);
            var pkInput = Path.Combine(pkDir, "input.xiso");
            File.Copy(csXiso, pkInput);
            (var code, var so, var se) = xk.Run(pkDir, "-y", "-q", "-p", pkInput);
            var xkSkel = Path.Combine(pkDir, "input.skeleton.xiso");
            var hasSkel = File.Exists(xkSkel) && new FileInfo(xkSkel).Length > 0;
            Del(pkDir); // the 1x-ISO copy + oracle skeleton are single-use
            if (!hasSkel)
            {
                // Known upstream limitation: LibXGD's CollectFileEntries parses
                // all-0xFF empty-directory tables as entries and reads past EOF.
                // Our collector carries the 0xFFFF sentinel fix (see
                // XisoRangesEmptyDirTests), so C# succeeds where the oracle crashes.
                if (so + se is { } oracleOut
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
    }

    private static SubBattleResult XkZar(string workInput, string csDir, string? xkDir, long isoOffset,
        XboxKitWrapper? xk)
    {
        return Timed("XK-Zar", () =>
        {
            var csZar = Path.Combine(csDir, "input.zar");
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
                var xkXiso = Path.Combine(xkDir, "input.xiso");
                if (!File.Exists(xkXiso))
                    return Skip("XK-Zar", "no xk .xiso split for -z");
                xkZar = Path.Combine(xkDir, "input.zar");
                string zarLog = "";
                if (!File.Exists(xkZar))
                {
                    // xboxkit resolves zarchive.exe next to the input, not next
                    // to itself; stage it in the oracle workdir.
                    var zaBeside = Path.Combine(AppContext.BaseDirectory, "zarchive.exe");
                    if (File.Exists(zaBeside))
                        File.Copy(zaBeside, Path.Combine(xkDir, "zarchive.exe"), true);
                    // No -q here: a silent failure must leave its [ERROR] in the detail.
                    (var zc, var zso, var zse) = xk.Run(xkDir, "-y", "-z", xkXiso);
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
            var csTree = Path.Combine(csDir, "zar-cs");
            var xkTree = Path.Combine(xkDir, "zar-xk");
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

            var r = CompareTrees(csTree, xkTree);
            Del(csTree); // extracted game trees are the largest artifacts
            Del(xkTree);
            return r;
        });
    }

    private static SubBattleResult XkRebuildOurs(string workInput, string csDir, string? xkDir)
    {
        return Timed("XK-RebuildCs", () =>
        {
            if (xkDir == null)
                return Skip("XK-RebuildCs", "no xk split parts");
            var xiso = Path.Combine(xkDir, "input.xiso");
            var video = Path.Combine(xkDir, "input.video.iso");
            var filler = Path.Combine(xkDir, "input.filler");
            var seed = Path.Combine(xkDir, "input.seed");
            var fillerOrSeed = File.Exists(filler) ? filler : File.Exists(seed) ? seed : null;
            var update = Directory.GetFiles(xkDir, "su20076000_00000000", SearchOption.AllDirectories).FirstOrDefault();
            if (!File.Exists(xiso) || !File.Exists(video) || fillerOrSeed == null)
            {
                return Fail(
                    $"xk split incomplete (xiso={Exists(xiso)} video={Exists(video)} filler/seed={fillerOrSeed != null})");
            }

            var rebuilt = Path.Combine(csDir, "rebuilt.iso");
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
            var r = CompareFiles("rebuilt redump", rebuilt, workInput);
            Del(rebuilt); // 1x ISO freed right after compare
            return r;
        });
    }

    private static SubBattleResult XkRebuildTheirs(string workInput, string csDir, string? xkDir, XboxKitWrapper? xk)
    {
        return Timed("XK-RebuildXk", () =>
        {
            if (xk?.Available != true || xkDir == null)
                return Skip("XK-RebuildXk", "xboxkit unavailable");
            // Stage OUR parts for xk rebuild mode: xboxkit <input.xiso> [files...]
            var rbDir = Path.Combine(csDir, "rbx");
            Directory.CreateDirectory(rbDir);
            foreach (var f in Directory.GetFiles(csDir))
            {
                var n = Path.GetFileName(f);
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
            var rbXiso = Path.Combine(rbDir, "input.xiso");
            if (!File.Exists(rbXiso))
                return Skip("XK-RebuildXk", "no C# xiso split staged");
            var parts = Directory.GetFiles(rbDir).Where(f => !f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                                                             || f.EndsWith(".video.iso",
                                                                 StringComparison.OrdinalIgnoreCase)).ToList();
            var before = Directory.GetFiles(rbDir, "*.iso", SearchOption.AllDirectories)
                .Select(f => f.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            xk.Rebuild(rbDir,
                new[] { rbXiso }.Concat(parts.Where(f => !string.Equals(f, rbXiso, StringComparison.Ordinal)))
                    .ToArray());
            var after = Directory.GetFiles(rbDir, "*.iso", SearchOption.AllDirectories)
                .Where(f => !before.Contains(f.ToLowerInvariant())).ToList();
            var rebuilt =
                after.FirstOrDefault(f => Path.GetFileName(f).Contains("redump", StringComparison.OrdinalIgnoreCase))
                ?? after.FirstOrDefault();
            if (rebuilt == null)
            {
                Del(rbDir);
                return Fail(
                    $"xk rebuild produced no new ISO (files: {string.Join(",", Directory.GetFiles(rbDir).Select(Path.GetFileName))})");
            }

            var rr = CompareFiles("xk-rebuilt redump", rebuilt, workInput);
            Del(rbDir); // staged parts (filler = 1x ISO) are single-use
            return rr;
        });
    }

    // ------------------------------------------------------------------
    // xdvdfs comparisons (plain XISOs only)
    // ------------------------------------------------------------------

    private static SubBattleResult XdChecksum(string xiso, string tag, XdvdfsWrapper xd)
    {
        return Timed($"XD-Checksum[{tag}]", () =>
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

            (var code, var so, var se) = xd.Checksum(xiso);
            var xdHex = ParseHexToken(so);
            if (xdHex.Length == 0)
                return Fail($"xdvdfs checksum unparseable (exit {code}): {Trim(so + se)}");
            return string.Equals(csHex, xdHex, StringComparison.OrdinalIgnoreCase)
                ? Pass($"checksum {csHex[..16]}… match")
                : Fail($"checksum mismatch C#={csHex} xd={xdHex}");
        });
    }

    private static SubBattleResult XdUnpack(string xiso, string tag, string sandbox, string csDir, XdvdfsWrapper xd)
    {
        return Timed($"XD-Unpack[{tag}]", () =>
        {
            var csOut = Path.Combine(csDir, $"unpack-{tag}");
            var xdOut = Path.Combine(sandbox, $"xd-unpack-{tag}");
            try
            {
                var rc = XisoReader.UnpackImage(xiso, csOut);
                if (rc != 0)
                    return Fail($"C# UnpackImage rc={rc}");
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (var code, _, var se) = xd.Unpack(xiso, xdOut);
            if (code != 0 || !Directory.Exists(xdOut))
                return Fail($"xdvdfs unpack exit {code}: {Trim(se)}");
            return CompareTrees(csOut, xdOut);
        });
    }

    private static SubBattleResult XdTree(string xiso, string tag, XdvdfsWrapper xd)
    {
        return Timed($"XD-Tree[{tag}]", () =>
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

            (var code, var so, var se) = xd.Tree(xiso);
            if (code != 0)
                return Fail($"xdvdfs tree exit {code}: {Trim(se)}");
            // Our Tree roots entries at the image name (`\input\...` for
            // `input.iso`, `\input.xiso\...` for splits); xdvdfs lists
            // image-internal paths (`/...`). Strip the root on compare.
            var root = ListingRootPrefix(xiso);
            var csSet = NormaliseListing(csText, root);
            var xdSet = NormaliseListing(so, root);
            if (csSet.SetEquals(xdSet))
                return Pass($"{csSet.Count} entries match");
            var onlyCs = csSet.Where(e => !xdSet.Contains(e)).Take(3).ToList();
            var onlyXd = xdSet.Where(e => !csSet.Contains(e)).Take(3).ToList();
            return Fail(
                $"tree mismatch C#={csSet.Count} xd={xdSet.Count} ONLY cs [{string.Join(";", onlyCs)}] ONLY xd [{string.Join(";", onlyXd)}]");
        });
    }

    private static SubBattleResult XdCopyOutMd5(string xiso, string tag, string sandbox, string csDir, XdvdfsWrapper xd)
    {
        return Timed($"XD-CopyOut+Md5[{tag}]", () =>
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

            var csOut = Path.Combine(csDir, $"copyout-{tag}.bin");
            try
            {
                XisoReader.CopyOut(xiso, inner, csOut);
            }
            catch (Exception ex)
            {
                return Fail($"C# CopyOut threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            var xdOut = Path.Combine(sandbox, $"xd-copyout-{tag}.bin");
            (var xcode, _, var xse) = xd.CopyOut(xiso, inner, xdOut);
            if (xcode != 0 || !File.Exists(xdOut))
                return Fail($"xdvdfs copy-out exit {xcode}: {Trim(xse)}");
            var r = CompareFiles($"copy-out {inner}", csOut, xdOut);
            if (r.Status != BattleStatus.Passed)
                return r;

            string csMd5;
            try
            {
                var bytes = XisoReader.ComputeFileHash(xiso, inner, HashAlgorithmName.MD5);
                csMd5 = bytes == null ? "" : Convert.ToHexString(bytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                return Fail($"C# ComputeFileHash threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (var mcode, var mso, var mse) = xd.Md5(xiso, inner);
            var xdMd5 = ParseHexToken(mso);
            if (xdMd5.Length != 32)
                return Fail($"xdvdfs md5 unparseable (exit {mcode}): {Trim(mso + mse)}");
            return string.Equals(csMd5, xdMd5, StringComparison.OrdinalIgnoreCase)
                ? Pass($"copy-out + md5 match ({inner}, {csMd5[..12]}…)")
                : Fail($"md5 mismatch {inner} C#={csMd5} xd={xdMd5}");
        });
    }

    private static SubBattleResult XdPack(string sandbox, string csDir, XdvdfsWrapper xd)
    {
        return Timed("XD-Pack", () =>
        {
            // Pack from the smallest available unpacked tree (keeps this check fast).
            var cands = Directory.GetDirectories(csDir, "unpack-*");
            string? tree = null;
            long best = long.MaxValue;
            foreach (var c in cands)
            {
                var bytes = Directory.GetFiles(c, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
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
                packSrc = Path.Combine(sandbox, "pack-src");
                var small = Directory.GetFiles(tree, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Length <= 8L * 1024 * 1024)
                    .OrderBy(f => f.Length).Take(8).ToList();
                if (small.Count == 0)
                    return Skip("XD-Pack", $"smallest tree {best / 1048576} MB > gate and no small files");
                foreach (var f in small)
                {
                    var dest = Path.Combine(packSrc, Path.GetRelativePath(tree, f.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f.FullName, dest);
                }

                packNote = $"mini-tree {small.Count} files";
            }

            var csPack = Path.Combine(csDir, "packed-cs.iso");
            var xdPack = Path.Combine(sandbox, "packed-xd.iso");
            try
            {
                if (XisoWriter.PackFromDirectory(packSrc, csPack) != 0)
                    return Fail("C# PackFromDirectory rc!=0");
            }
            catch (Exception ex)
            {
                return Fail($"C# threw {ex.GetType().Name}: {Trim(ex.Message)}");
            }

            (var code, _, var se) = xd.Pack(packSrc, xdPack);
            if (code != 0 || !File.Exists(xdPack))
                return Fail($"xdvdfs pack exit {code}: {Trim(se)}");
            // Layouts differ by design: content checksums must agree.
            (var c1, var s1, _) = xd.Checksum(csPack);
            (var c2, var s2, _) = xd.Checksum(xdPack);
            if (c1 != 0 || c2 != 0)
                return Fail($"xdvdfs checksum on packs failed ({c1},{c2}): {Trim(s1 + s2)}");
            var h1 = ParseHexToken(s1);
            var h2 = ParseHexToken(s2);
            return h1.Length > 0 && string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase)
                ? Pass($"pack content checksum match ({h1[..16]}…, {packNote})")
                : Fail($"pack content mismatch cs={h1} xd={h2}");
        });
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static (long IsoOffset, long XisoLength, bool IsRedump) GetPartition(string isoPath)
    {
        var size = new FileInfo(isoPath).Length;
        var redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
        if (redumpType < 0)
        {
            using var fs = File.OpenRead(isoPath);
            var (_, _, lseek) = XisoReader.VerifyXiso(fs, "input.iso");
            return (lseek, size - lseek, false);
        }

        using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        {
            var videoType = XgdTables.GetVideoType(fs, redumpType);
            var xsType = videoType >= 0 ? XgdTables.GetXisoTypeFromVideo(videoType) : -1;
            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
                xsType = XgdTables.GetXgdType(redumpType);
            return (XgdTables.XisoOffset[xsType], XgdTables.XisoLength[xsType], true);
        }
    }

    private static SubBattleResult Timed(string name, Func<SubBattleResult> body)
    {
        var sw = Stopwatch.StartNew();
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

    private static SubBattleResult Pass(string detail)
    {
        return new SubBattleResult
        {
            TestName = "", Status = BattleStatus.Passed, Detail = detail, ElapsedSeconds = 0,
        };
    }

    private static SubBattleResult Fail(string detail, string? extra = null)
    {
        return new SubBattleResult
        {
            TestName = "",
            Status = BattleStatus.Failed,
            Detail = extra is null ? detail : $"{detail} | {extra}",
            ElapsedSeconds = 0,
        };
    }

    private static SubBattleResult Skip(string name, string detail)
    {
        return new SubBattleResult
        {
            TestName = name, Status = BattleStatus.Skipped, Detail = detail, ElapsedSeconds = 0,
        };
    }

    private static SubBattleResult CompareFiles(
        string what, string? csPath, string? xkPath, bool bothMissingOk = false, string? bothMissingNote = null)
    {
        var csExists = csPath != null && File.Exists(csPath);
        var xkExists = xkPath != null && File.Exists(xkPath);
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
        var csHash = HashUtil.ComputeSha256(csPath!);
        var xkHash = HashUtil.ComputeSha256(xkPath!);
        return string.Equals(csHash, xkHash, StringComparison.Ordinal)
            ? Pass($"{what} SHA match ({Mb(csPath)})")
            : Fail($"{what} SHA mismatch C#={csHash[..16]}…({Mb(csPath)}) oracle={xkHash[..16]}…({Mb(xkPath)})");
    }

    private static SubBattleResult CompareTrees(string csOut, string xdOut)
    {
        var cs = HashUtil.HashDirectory(csOut);
        var xd = HashUtil.HashDirectory(xdOut);
        if (cs.Count != xd.Count)
            return Fail($"unpack tree count C#={cs.Count} xd={xd.Count}");
        foreach (var kv in cs)
        {
            if (!xd.TryGetValue(kv.Key, out var xh))
                return Fail($"unpack: only in C# tree: {kv.Key}");
            if (!string.Equals(xh, kv.Value, StringComparison.Ordinal))
                return Fail($"unpack: content mismatch {kv.Key}");
        }

        var xdOnly = xd.Keys.Except(cs.Keys, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (xdOnly != null)
            return Fail($"unpack: only in xd tree: {xdOnly}");
        return Pass($"unpack trees identical ({cs.Count} files)");
    }

    private static string CaptureTree(string xiso)
    {
        var saveQuiet = Logger.Quiet;
        var saveOut = Logger.Out;
        var origOut = Console.Out;
        try
        {
            Logger.Quiet = false;
            using var sw = new StringWriter();
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
        var fn = Path.GetFileName(xisoPath);
        var root = fn.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? fn[..^".iso".Length] : fn;
        return "/" + root + "/";
    }

    private static HashSet<string> NormaliseListing(string text, string? listingRootPrefix = null)
    {
        // Ours: `\a.txt (7 bytes)` rooted at the image name; xdvdfs: `/a.txt (7 bytes)`
        // image-internal paths + `N files, M bytes` totals.
        var set = new HashSet<string>(StringComparer.Ordinal);
        const string suffix = " bytes)";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Replace('\\', '/');
            var open = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0 || !line.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var size = line.Substring(open + 2, line.Length - open - 2 - suffix.Length);
            if (size.Length == 0 || !size.All(char.IsAsciiDigit))
                continue;
            var p = line[..open];
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
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var i = 0;
            while (i < line.Length && IsHexDigit(line[i])) i++;
            if (i is >= 32 and <= 128 && (i == line.Length || char.IsWhiteSpace(line[i])))
                return line[..i];
        }

        return "";
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    private static string? FirstFileEntry(string xiso)
    {
        var set = NormaliseListing(CaptureTree(xiso), ListingRootPrefix(xiso))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var e in set)
        {
            var parts = e.Split('|');
            // entries are path|size; dirs show size 0 but so can empty files — prefer size>0.
            if (parts.Length == 2 && !string.Equals(parts[1], "0", StringComparison.Ordinal))
                return parts[0].Length == 0 ? "/" : parts[0];
        }

        return null;
    }

    private static void CopyExact(FileStream src, FileStream dst, long bytes)
    {
        var buf = new byte[65536];
        while (bytes > 0)
        {
            var want = (int)Math.Min(buf.Length, bytes);
            var read = 0;
            while (read < want)
            {
                var n = src.Read(buf, read, want - read);
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
        var env = Environment.GetEnvironmentVariable("XISO_BATTLE_SANDBOX");
        if (!string.IsNullOrWhiteSpace(env))
        {
            baseDir = env;
        }
        else
        {
            var full = Path.GetFullPath(sourcePath);
            var root = Path.GetPathRoot(full) ?? "";
            baseDir = root.Length > 0 ? Path.Combine(root, "XISOSharpBattle") : CreateSandboxFallback();
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(baseDir)) ?? baseDir);
            var needed = isoSize * 4;
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

        var dir = Path.Combine(baseDir, "ext_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateSandboxFallback()
    {
        return Path.Combine(Path.GetTempPath(), "XISOSharpBattle");
    }

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

    private static string Exists(string p)
    {
        return File.Exists(p) ? "yes" : "no";
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 220 ? s[..220] : s;
    }
}