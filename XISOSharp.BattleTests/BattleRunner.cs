using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XISOSharp.BlockDevice;
using XISOSharp.BattleTests.Models;
using XISOSharp.Models;

namespace XISOSharp.BattleTests;

/// <summary>Orchestrates all battle comparisons between C# and native extract-xiso.</summary>
internal static class BattleRunner
{
    /// <summary>
    /// Runs the full battle suite against the supplied ISO files.
    /// </summary>
    /// <param name="isoFiles">ISO paths to compare between C# and native implementations.</param>
    /// <param name="exePath">Path to the native extract-xiso executable; C#-only checks run when missing.</param>
    /// <param name="createDirs">Optional source directories for create-parity battles.</param>
    /// <returns>The aggregated session result with per-file and advanced-check outcomes.</returns>
    public static async Task<BattleSessionResult> RunAsync(IList<string> isoFiles, string exePath,
        string[]? createDirs = null)
    {
        var session = new BattleSessionResult();
        var sw = Stopwatch.StartNew();

        var exeExists = File.Exists(exePath);
        using var wrapper = exeExists ? new ExtractXisoWrapper(exePath) : null;
        if (wrapper != null)
        {
            try
            {
                session.NativeVersion = wrapper.GetVersion();
            }
            catch
            {
                session.NativeVersion = "unknown";
            }
        }

        Console.WriteLine(
            $"Battle: {isoFiles.Count} ISO(s) — native: {(wrapper != null ? session.NativeVersion : "NOT FOUND (C# only)")}");
        if (createDirs?.Length > 0)
            Console.WriteLine($"Create dirs: {string.Join(", ", createDirs)}");

        for (var i = 0; i < isoFiles.Count; i++)
        {
            var file = isoFiles[i];
            var fi = new FileInfo(file);
            if (!fi.Exists)
            {
                Console.WriteLine($"[{i + 1}/{isoFiles.Count}] SKIP {file} (not found)");
                continue;
            }

            Console.Write($"[{i + 1}/{isoFiles.Count}] {fi.Name} ({fi.Length / (1024.0 * 1024):F1} MB) ... ");
            var wrapperLocal = wrapper;
            var result = await Task.Run(() => TestSingleFile(file, wrapperLocal)).ConfigureAwait(false);
            session.FileResults.Add(result);
            var status = result.HasFailures ? "FAIL" : "PASS";
            var color = result.HasFailures ? ConsoleColor.Red : ConsoleColor.Green;
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(
                $"{status} ({result.ElapsedSeconds:F1}s) {string.Join(" ", result.SubTests.Select(s => $"{s.TestName}:{Symbol(s.Status)}"))}");
            Console.ForegroundColor = prev;
            foreach (var sub in result.SubTests.Where(s => s.Status == BattleStatus.Failed))
                Console.WriteLine($"  \u2717 {sub.TestName}: {sub.Detail.Split('\n').FirstOrDefault()?.Trim()}");
        }

        if (createDirs != null)
        {
            foreach (var dir in createDirs)
            {
                if (!Directory.Exists(dir))
                {
                    Console.WriteLine($"Create battle: dir not found {dir}");
                    continue;
                }

                Console.Write($"[CREATE] {dir} ... ");
                var wrapperLocal2 = wrapper;
                var cr = await Task.Run(() => TestCreateBattle(dir, wrapperLocal2)).ConfigureAwait(false);
                session.FileResults.Add(cr);
                var color = cr.HasFailures ? ConsoleColor.Red : ConsoleColor.Green;
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(
                    $"{(cr.HasFailures ? "FAIL" : "PASS")} ({cr.ElapsedSeconds:F1}s) {string.Join(" ", cr.SubTests.Select(s => $"{s.TestName}:{Symbol(s.Status)}"))}");
                Console.ForegroundColor = prev;
                foreach (var sub in cr.SubTests.Where(s => s.Status == BattleStatus.Failed))
                    Console.WriteLine($"  \u2717 {sub.TestName}: {sub.Detail.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }

        Console.WriteLine("\n[ADVANCED] Running C# advanced feature checks (no native counterpart) ...");
        var adv = await Task.Run(() => RunAdvancedChecks(isoFiles.FirstOrDefault())).ConfigureAwait(false);
        session.FileResults.Add(adv);
        Console.WriteLine($"  {string.Join(" ", adv.SubTests.Select(s => $"{s.TestName}:{Symbol(s.Status)}"))}");
        foreach (var sub in adv.SubTests.Where(s => s.Status == BattleStatus.Failed))
            Console.WriteLine($"  \u2717 {sub.TestName}: {sub.Detail}");

        sw.Stop();
        session.Elapsed = sw.Elapsed;

        return session;
    }

    private static string Symbol(BattleStatus s) =>
        s switch
        {
            BattleStatus.Passed => "\u2713", BattleStatus.Failed => "\u2717", BattleStatus.Skipped => "-", _ => "?"
        };

    private static PerFileBattleResult TestSingleFile(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        var fi = new FileInfo(path);
        var result = new PerFileBattleResult { FilePath = path, FileName = fi.Name, FileSize = fi.Length };

        result.SubTests.Add(RunVerify(path, wrapper));
        result.SubTests.Add(RunAudit(path));
        result.SubTests.Add(RunList(path, wrapper));
        result.SubTests.Add(RunExtract(path, wrapper));
        result.SubTests.Add(RunRewrite(path, wrapper));
        result.SubTests.Add(RunCisoRoundTrip(path));
        result.SubTests.Add(RunChecksum(path, wrapper));
        result.SubTests.Add(RunBlockDevice(path));

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static SubBattleResult RunVerify(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var fs = File.OpenRead(path);
            (var rootSector, var rootSize, var lseek) = XisoReader.VerifyXiso(fs, Path.GetFileName(path));
            var csDetail = $"Valid RootSector={rootSector} RootSize={rootSize} Lseek=0x{lseek:X}";
            if (wrapper?.Available == true)
            {
                // BTL-003: a native `-l` exit code is a LIST result, not a VERIFY
                // result. The verdict rests on the C# VerifyXiso above; native
                // output is informational only.
                string nativeNote;
                try
                {
                    (var code, _, var se) = wrapper.ListFiles(path);
                    nativeNote = code == 0
                        ? "native list exit 0 (listing only, not a verify)"
                        : $"native list exit {code}: {se.Trim()} (listing only, not a verify)";
                }
                catch (Exception nex)
                {
                    nativeNote =
                        $"native list error {nex.GetType().Name}: {(nex.Message.Split('\n').FirstOrDefault() ?? string.Empty).Trim()} (listing only)";
                }

                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Verify",
                    Status = BattleStatus.Passed,
                    Detail = $"C#: {csDetail} | {nativeNote}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Verify",
                Status = BattleStatus.Passed,
                Detail = csDetail,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Verify",
                Status = BattleStatus.Skipped,
                Detail = "Empty XISO (no files)",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            if (wrapper?.Available == true)
            {
                int code;
                string nativeOut;
                try
                {
                    (code, var so, var se) = wrapper.ListFiles(path);
                    nativeOut = so + "\n" + se;
                }
                catch (Exception nex)
                {
                    sw.Stop();
                    return new SubBattleResult
                    {
                        TestName = "Verify",
                        Status = BattleStatus.Failed,
                        Detail =
                            $"C# error: {(ex.Message.Split('\n').FirstOrDefault() ?? string.Empty).Trim()} | native list error {nex.GetType().Name}: {(nex.Message.Split('\n').FirstOrDefault() ?? string.Empty).Trim()}",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }

                sw.Stop();
                if (code != 0)
                {
                    // BTL-004: both sides failing is not enough — the reasons
                    // must agree, otherwise divergent corruptions are masked.
                    var csKind = ClassifyVerifyFailure(ex);
                    var nativeKind = ClassifyVerifyFailure(nativeOut);
                    var csFirst = (ex.Message.Split('\n').FirstOrDefault() ?? string.Empty).Trim();
                    var nativeFirst = (nativeOut.Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ??
                                       string.Empty).Trim();
                    if (string.Equals(csKind, nativeKind, StringComparison.Ordinal) &&
                        !string.Equals(csKind, "other", StringComparison.Ordinal))
                    {
                        return new SubBattleResult
                        {
                            TestName = "Verify",
                            Status = BattleStatus.Passed,
                            Detail =
                                $"Both fail as expected ({csKind}): C#: {csFirst} | native exit {code}: {nativeFirst}",
                            ElapsedSeconds = sw.Elapsed.TotalSeconds
                        };
                    }

                    return new SubBattleResult
                    {
                        TestName = "Verify",
                        Status = BattleStatus.Failed,
                        Detail =
                            $"Divergent failures C#({csKind}): {csFirst} | native({nativeKind}) exit {code}: {nativeFirst}",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }

                return new SubBattleResult
                {
                    TestName = "Verify",
                    Status = BattleStatus.Failed,
                    Detail =
                        $"Divergent: C# failed ({(ex.Message.Split('\n').FirstOrDefault() ?? string.Empty).Trim()}) but native list exit 0",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Verify",
                Status = BattleStatus.Failed,
                Detail = $"C# error: {ex.Message}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static string ClassifyVerifyFailure(Exception ex)
    {
        if (ex is XisoEmptyException)
        {
            return "empty";
        }

        return ClassifyVerifyFailure(ex.Message);
    }

    private static string ClassifyVerifyFailure(string message)
    {
        if (message.Contains("no files", StringComparison.OrdinalIgnoreCase))
        {
            return "empty";
        }

        if (message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not appear", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not a valid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no XISO header", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
        {
            return "format";
        }

        if (message.Contains("truncat", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("short", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("end of stream", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("beyond end", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exceeds", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("outside", StringComparison.OrdinalIgnoreCase))
        {
            return "truncated";
        }

        return "other";
    }

    private static SubBattleResult RunAudit(string path)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var res = XisoReader.AuditXiso(path);
            sw.Stop();
            if (res.IsValid)
            {
                return new SubBattleResult
                {
                    TestName = "Audit",
                    Status = BattleStatus.Passed,
                    Detail = $"Valid files={res.FilesChecked} dirs={res.DirsChecked}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
            else if (res.Issues.Count == 1 &&
                     res.Issues[0].Contains("Optimized tag not found", StringComparison.Ordinal))
            {
                // Pristine (never-rewritten) dump: every file/dir checked clean, only the
                // "already optimized" marker is absent. That is valid input, just unoptimized.
                return new SubBattleResult
                {
                    TestName = "Audit",
                    Status = BattleStatus.Passed,
                    Detail =
                        $"Pristine unoptimized files={res.FilesChecked} dirs={res.DirsChecked} (tag absent) \u2713",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
            else
            {
                return new SubBattleResult
                {
                    TestName = "Audit",
                    Status = BattleStatus.Failed,
                    Detail =
                        $"Invalid files={res.FilesChecked} issues={res.Issues.Count} first={res.Issues.FirstOrDefault()}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (ex is StackOverflowException)
            {
                return new SubBattleResult
                {
                    TestName = "Audit",
                    Status = BattleStatus.Skipped,
                    Detail = "Skipped due to stack overflow",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            return new SubBattleResult
            {
                TestName = "Audit",
                Status = BattleStatus.Failed,
                Detail = ex.GetType().Name + ": " + ex.Message.Split('\n').FirstOrDefault(),
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult RunList(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // BTL-014: prefer the structured TOC walk; the regex text parse
            // stays only as a fallback for images GetSectorLayout cannot read.
            var csEntries = TryGetStructuredEntries(path) ?? ParseListOutput(CaptureCSharpList(path));
            if (wrapper?.Available != true)
            {
                sw.Stop();
                return csEntries.Count > 0
                    ? new SubBattleResult
                    {
                        TestName = "List",
                        Status = BattleStatus.Passed,
                        Detail = $"C# {csEntries.Count} entries",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    }
                    : new SubBattleResult
                    {
                        TestName = "List",
                        Status = BattleStatus.Skipped,
                        Detail = "No entries",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
            }

            (var code, var so, var se) = wrapper.ListFiles(path);
            if (code != 0)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "List",
                    Status = BattleStatus.Failed,
                    Detail = $"native list exit {code}: {se.Trim()}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var exeEntries = ParseListOutput(so);
            var cmp = CompareLists(csEntries, exeEntries);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "List",
                Status = cmp.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = cmp.Detail,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "List",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult RunExtract(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        var csDir = CreateTempDir("cs_ext");
        var exeDir = CreateTempDir("exe_ext");
        try
        {
            try
            {
                var q = Logger.Quiet;
                Logger.Quiet = true;
                try
                {
                    XisoReader.Extract(path, csDir, false);
                }
                finally
                {
                    Logger.Quiet = q;
                }
            }
            catch (ExtractErrorException ex) when (ex.ErrorCode == ExtractError.ErrIsoNoFiles)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Extract",
                    Status = BattleStatus.Skipped,
                    Detail = "Empty (no files)",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            if (wrapper?.Available != true)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Extract",
                    Status = BattleStatus.Passed,
                    Detail = $"C# {CountFiles(csDir)} files",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            (var code, _, var se) = wrapper.ExtractFiles(path, exeDir);
            if (code != 0)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Extract",
                    Status = BattleStatus.Failed,
                    Detail = $"native extract exit {code}: {se.Trim()}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var cmp = CompareDirs(csDir, exeDir);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Extract",
                Status = cmp.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = cmp.Detail,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Extract",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        finally
        {
            DeleteDir(csDir);
            DeleteDir(exeDir);
        }
    }

    private static SubBattleResult RunRewrite(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        if (wrapper?.Available != true)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Rewrite",
                Status = BattleStatus.Skipped,
                Detail = "native not available",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        var csWork = CreateTempDir("cs_rw");
        var exeWork = CreateTempDir("exe_rw");
        try
        {
            var csInput = Path.Combine(csWork, Path.GetFileName(path));
            File.Copy(path, csInput, true);

            using (var fs = File.OpenRead(csInput))
            {
                if (fs.Length > Constants.OptimizedTagOffset + Constants.OptimizedTag.Length)
                {
                    try
                    {
                        fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
                        Span<byte> buf = stackalloc byte[Constants.OptimizedTag.Length];
                        fs.ReadExactly(buf);
                        var tag = Encoding.ASCII.GetString(buf);
                        if (string.Equals(tag, Constants.OptimizedTag, StringComparison.Ordinal))
                        {
                            sw.Stop();
                            return new SubBattleResult
                            {
                                TestName = "Rewrite",
                                Status = BattleStatus.Skipped,
                                Detail = "Already optimized",
                                ElapsedSeconds = sw.Elapsed.TotalSeconds
                            };
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        // BTL-016: the Length check above can race a shrinking/truncated
                        // file — a short tag read is truncated input (Skipped), not a
                        // generic rewrite failure.
                        sw.Stop();
                        return new SubBattleResult
                        {
                            TestName = "Rewrite",
                            Status = BattleStatus.Skipped,
                            Detail = "Skipped: truncated input (short read at optimized-tag offset)",
                            ElapsedSeconds = sw.Elapsed.TotalSeconds
                        };
                    }
                }
            }

            var csOutDir = Path.Combine(csWork, "out");
            Directory.CreateDirectory(csOutDir);
            try
            {
                var q = Logger.Quiet;
                Logger.Quiet = true;
                try
                {
                    XisoReader.Rewrite(csInput, csOutDir, out _);
                }
                finally
                {
                    Logger.Quiet = q;
                }
            }
            catch (ExtractErrorException ex) when (ex.ErrorCode is ExtractError.ErrIsoRewritten
                                                       or ExtractError.ErrIsoNoFiles)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Rewrite",
                    Status = BattleStatus.Skipped,
                    Detail = $"Skipped: {ex.Message}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var csOut = FindRewriteOutput(csWork, csOutDir);
            var exeInput = Path.Combine(exeWork, Path.GetFileName(path));
            File.Copy(path, exeInput, true);
            var exeOutDir = Path.Combine(exeWork, "out");
            Directory.CreateDirectory(exeOutDir);
            (var code, _, var se) = wrapper.Rewrite(exeInput, exeOutDir);
            if (code != 0)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Rewrite",
                    Status = BattleStatus.Failed,
                    Detail = $"native rewrite exit {code}: {se.Trim()}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var exeOut = FindRewriteOutput(exeWork, exeOutDir);
            if (csOut == null || exeOut == null)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Rewrite",
                    Status = BattleStatus.Failed,
                    Detail = $"Output not found C#:{csOut ?? "null"} exe:{exeOut ?? "null"}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var csHash = HashUtil.ComputeSha256(csOut);
            var exeHash = HashUtil.ComputeSha256(exeOut);
            var match = string.Equals(csHash, exeHash, StringComparison.Ordinal);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Rewrite",
                Status = match ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = match ? $"SHA256 {csHash} \u2713" : $"SHA256 mismatch C#:{csHash} exe:{exeHash}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Rewrite",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        finally
        {
            DeleteDir(csWork);
            DeleteDir(exeWork);
        }
    }

    /// <summary>
    /// Locates a rewrite-battle output deterministically (BTL-015): both the C# and
    /// native sides use this same rule — recursive <c>*.iso</c> search under
    /// <paramref name="workDir"/> excluding rewrite backups (<c>*.old</c>),
    /// preferring candidates under <paramref name="outDir"/> (so the staged input
    /// copy at the work root is never picked), newest first with an ordinal path
    /// tie-break so stale/wrong files are never picked.
    /// </summary>
    private static string? FindRewriteOutput(string workDir, string outDir)
    {
        var prefix = outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return Directory.GetFiles(workDir, "*.iso", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>CISO compression level for the round-trip battle (fast; correctness, not ratio, is compared).</summary>
    private const int CisoCompressionLevel = 1;

    /// <summary>
    /// Inputs larger than this skip SHA-256 verification in the CISO round-trip
    /// (BTL-017): hashing a multi-GB image twice per battle is prohibitive.
    /// Size equality is still checked; only the byte-hash compare is skipped.
    /// </summary>
    private const long CisoHashVerifyGateBytes = 2L * 1024 * 1024 * 1024;

    private static SubBattleResult RunCisoRoundTrip(string path, int level = CisoCompressionLevel)
    {
        var sw = Stopwatch.StartNew();
        var tmp = CreateTempDir("ciso");
        try
        {
            var cso = Path.Combine(tmp, "test.cso");
            var dec = Path.Combine(tmp, "test.dec.iso");
            try
            {
                CisoWriter.CompressToCso(path, cso, level: level);
                if (!CisoReader.IsCso(cso))
                {
                    sw.Stop();
                    return new SubBattleResult
                    {
                        TestName = "CISO",
                        Status = BattleStatus.Failed,
                        Detail = "IsCso false after compress",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }

                CisoReader.DecompressToIso(cso, dec);
                var inputBytes = new FileInfo(path).Length;
                if (inputBytes > CisoHashVerifyGateBytes)
                {
                    // BTL-017: skip the double multi-GB hash above the gate — a
                    // size match after the round-trip is the whole check here.
                    var decBytes = new FileInfo(dec).Length;
                    sw.Stop();
                    if (decBytes != inputBytes)
                    {
                        return new SubBattleResult
                        {
                            TestName = "CISO",
                            Status = BattleStatus.Failed,
                            Detail =
                                $"Round-trip size mismatch {inputBytes} vs {decBytes} (hash skipped above {CisoHashVerifyGateBytes} byte gate)",
                            ElapsedSeconds = sw.Elapsed.TotalSeconds
                        };
                    }

                    return new SubBattleResult
                    {
                        TestName = "CISO",
                        Status = BattleStatus.Skipped,
                        Detail =
                            $"Skipped hash verification: input {inputBytes} bytes exceeds {CisoHashVerifyGateBytes} byte gate; round-trip size match \u2713 (level {level})",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }

                var origHash = HashUtil.ComputeSha256(path);
                var decHash = HashUtil.ComputeSha256(dec);
                var match = string.Equals(origHash, decHash, StringComparison.Ordinal);
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "CISO",
                    Status = match ? BattleStatus.Passed : BattleStatus.Failed,
                    Detail = match ? $"Round-trip SHA256 {origHash} \u2713" : $"Mismatch {origHash} vs {decHash}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "CISO",
                    Status = BattleStatus.Failed,
                    Detail = ex.Message,
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    private static SubBattleResult RunChecksum(string path, ExtractXisoWrapper? wrapper)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var csHex = XisoChecksum.ComputeImageChecksumHex(path);
            sw.Stop();
            if (csHex.Length != 64)
            {
                return new SubBattleResult
                {
                    TestName = "Checksum",
                    Status = BattleStatus.Failed,
                    Detail = $"Invalid hex length {csHex.Length}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            if (wrapper?.Available != true)
            {
                return new SubBattleResult
                {
                    TestName = "Checksum",
                    Status = BattleStatus.Skipped,
                    Detail = $"No native checksum oracle (native not available); C#={csHex}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            // extract-xiso v2.7.1 exposes no checksum oracle (List/Extract/Rewrite/Create
            // only), so native parity output is unavailable for this path. Report Skipped
            // explicitly rather than a vacuous self-consistency pass.
            return new SubBattleResult
            {
                TestName = "Checksum",
                Status = BattleStatus.Skipped,
                Detail = $"No native checksum oracle for extract-xiso; C#={csHex}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Checksum",
                Status = BattleStatus.Skipped,
                Detail = $"Not XISO? {ex.Message.Split('\n').FirstOrDefault()}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult RunBlockDevice(string path)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // BTL-023: path-based ctor — the device opens and owns its FileStream,
            // so `using var fbd` disposes exactly one handle with no ambiguity
            // (the stream-ctor overload defaults to ownership too, but leaves the
            // caller holding a second reference to the same handle).
            using var fbd = new FileBlockDevice(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fbd.Length != new FileInfo(path).Length)
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "BlockDev",
                    Status = BattleStatus.Failed,
                    Detail = $"Length mismatch {fbd.Length} vs {new FileInfo(path).Length}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            if (fbd.Length > Constants.HeaderOffset + 32)
            {
                Span<byte> buf = stackalloc byte[20];
                var n = fbd.Read(Constants.HeaderOffset, buf);
                // BTL-013: a short read means a truncated/unreadable header —
                // fail instead of passing with unchecked contents.
                if (n != buf.Length)
                {
                    sw.Stop();
                    return new SubBattleResult
                    {
                        TestName = "BlockDev",
                        Status = BattleStatus.Failed,
                        Detail = $"Short read at header offset: expected {buf.Length} got {n}",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }
            }

            var mdb = new MemoryBlockDevice(64 * 1024);
            Span<byte> test = stackalloc byte[] { 1, 2, 3, 4 };
            mdb.Write(0, test);
            Span<byte> outBuf = stackalloc byte[4];
            mdb.Read(0, outBuf);
            if (!outBuf.SequenceEqual(test))
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "BlockDev",
                    Status = BattleStatus.Failed,
                    Detail = "MemoryBlockDevice read/write mismatch",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            sw.Stop();
            return new SubBattleResult
            {
                TestName = "BlockDev",
                Status = BattleStatus.Passed,
                Detail = $"FileBlockDevice len={fbd.Length} \u2713",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "BlockDev",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static PerFileBattleResult TestCreateBattle(string dir, ExtractXisoWrapper? wrapper)
    {
        dir = Path.GetFullPath(dir);
        var sw = Stopwatch.StartNew();
        var name = new DirectoryInfo(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        var result =
            new PerFileBattleResult { FilePath = dir, FileName = $"create:{name}", FileSize = CountFiles(dir) };

        var tmp = CreateTempDir("create_battle");
        try
        {
            var csIso = Path.Combine(tmp, "cs.iso");
            var exeIso = Path.Combine(tmp, "exe.iso");

            // BTL-012: each create subtest records its own stopwatch.
            var csSw = Stopwatch.StartNew();
            try
            {
                var q = Logger.Quiet;
                Logger.Quiet = true;
                try
                {
                    XisoWriter.PackFromDirectory(dir, csIso);
                }
                finally
                {
                    Logger.Quiet = q;
                }
            }
            catch (Exception ex)
            {
                csSw.Stop();
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-C#",
                    Status = BattleStatus.Failed,
                    Detail = ex.Message,
                    ElapsedSeconds = csSw.Elapsed.TotalSeconds
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            csSw.Stop();
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-C#",
                Status = BattleStatus.Passed,
                Detail = $"C# created {new FileInfo(csIso).Length} bytes",
                ElapsedSeconds = csSw.Elapsed.TotalSeconds
            });

            var nativeSw = Stopwatch.StartNew();
            if (wrapper?.Available != true)
            {
                nativeSw.Stop();
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Native",
                    Status = BattleStatus.Skipped,
                    Detail = "native not available",
                    ElapsedSeconds = nativeSw.Elapsed.TotalSeconds
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            (var code, var so, var se) = wrapper.Create(dir, exeIso);
            if (code != 0)
            {
                // BTL-011: no process-wide CWD mutation. The implicit-name
                // fallback runs with a per-process WorkingDirectory; every
                // path handed to the oracle is absolute.
                var work = Path.Combine(tmp, "exe_fallback");
                Directory.CreateDirectory(work);
                try
                {
                    (var c2, var o2, var e2) = wrapper.RunInDirectory(work, "-c", dir);
                    code = c2;
                    so = o2;
                    se = e2;
                    if (code == 0)
                    {
                        var found = Directory.GetFiles(work, "*.iso").FirstOrDefault();
                        if (found != null)
                        {
                            File.Copy(found, exeIso, true);
                        }
                    }
                }
                catch (Exception fex)
                {
                    code = 1;
                    se = (se + "\nfallback: " + fex.Message).Trim();
                }
            }

            if (code != 0 || !File.Exists(exeIso))
            {
                nativeSw.Stop();
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Native",
                    Status = BattleStatus.Failed,
                    Detail = $"native create exit {code}: {se.Trim()} {so.Trim()}",
                    ElapsedSeconds = nativeSw.Elapsed.TotalSeconds
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            nativeSw.Stop();
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-Native",
                Status = BattleStatus.Passed,
                Detail = $"native created {new FileInfo(exeIso).Length} bytes",
                ElapsedSeconds = nativeSw.Elapsed.TotalSeconds
            });

            var listSw = Stopwatch.StartNew();
            var csList = TryGetStructuredEntries(csIso) ?? ParseListOutput(CaptureCSharpList(csIso));
            var exeList = ParseListOutput(wrapper.ListFiles(exeIso).StdOut);
            var cmpList = CompareLists(csList, exeList);
            listSw.Stop();
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-List",
                Status = cmpList.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = cmpList.Detail,
                ElapsedSeconds = listSw.Elapsed.TotalSeconds
            });

            var csExt = Path.Combine(tmp, "cs_ext2");
            Directory.CreateDirectory(csExt);
            var exeExt = Path.Combine(tmp, "exe_ext2");
            Directory.CreateDirectory(exeExt);
            var extractSw = Stopwatch.StartNew();
            try
            {
                var q = Logger.Quiet;
                Logger.Quiet = true;
                try
                {
                    XisoReader.Extract(csIso, csExt, false);
                }
                finally
                {
                    Logger.Quiet = q;
                }

                (var ec, _, var ese) = wrapper.ExtractFiles(exeIso, exeExt);
                if (ec != 0)
                {
                    extractSw.Stop();
                    result.SubTests.Add(new SubBattleResult
                    {
                        TestName = "Create-Extract",
                        Status = BattleStatus.Failed,
                        Detail = $"native extract exit {ec}: {ese.Trim()}",
                        ElapsedSeconds = extractSw.Elapsed.TotalSeconds
                    });
                }
                else
                {
                    var cmpD = CompareDirs(csExt, exeExt);
                    extractSw.Stop();
                    result.SubTests.Add(new SubBattleResult
                    {
                        TestName = "Create-Extract",
                        Status = cmpD.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                        Detail = cmpD.Detail,
                        ElapsedSeconds = extractSw.Elapsed.TotalSeconds
                    });
                }
            }
            catch (Exception ex)
            {
                extractSw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Extract",
                    Status = BattleStatus.Failed,
                    Detail = ex.Message,
                    ElapsedSeconds = extractSw.Elapsed.TotalSeconds
                });
            }

            sw.Stop();
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    private static PerFileBattleResult RunAdvancedChecks(string? sampleIso)
    {
        var sw = Stopwatch.StartNew();
        var result = new PerFileBattleResult { FilePath = "advanced", FileName = "advanced-self-tests", FileSize = 0 };

        result.SubTests.Add(CheckRemapFilesystem());
        result.SubTests.Add(CheckWaxGlob());
        result.SubTests.Add(CheckXisoRanges(sampleIso));
        result.SubTests.Add(CheckXgdTables());
        result.SubTests.Add(CheckSecuritySectors());
        result.SubTests.Add(CheckXboxPrng());
        result.SubTests.Add(CheckXisoOperations(sampleIso));
        result.SubTests.Add(CheckGlobMatcher());

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    private static SubBattleResult CheckRemapFilesystem()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ok = RemapRule.TryParse("**/*.txt:docs/{1}", out var rule, out _) && rule != null &&
                     string.Equals(rule.HostGlob, "**/*.txt", StringComparison.Ordinal);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Remap",
                Status = ok ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = ok ? "TryParse ok" : "TryParse failed",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Remap",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckWaxGlob()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var g = new WaxGlob("**/*.txt");
            var ok = g.IsMatch("a/b.txt") && !g.IsMatch("a/b.png");
            var caps = g.GetCaptures("a/b.txt");
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "WaxGlob",
                Status = ok ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = ok ? $"match ok caps={caps?.Count}" : "mismatch",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "WaxGlob",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckXisoRanges(string? iso)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (iso == null || !File.Exists(iso))
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Ranges",
                    Status = BattleStatus.Skipped,
                    Detail = "No sample ISO",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
            (var sys, var files) =
                XisoRanges.GetXisoRanges(fs, 0, true);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Ranges",
                Status = (sys.Count > 0 || files.Count > 0) ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = $"sys={sys.Count} fileRanges={files.Count}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Ranges",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckXgdTables()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var idx = XgdTables.GetRedumpIsoTypeBySize(7825162240);
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "XgdTables",
                Status = idx >= 0 ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = $"type for 7825162240 idx={idx}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "XgdTables",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckSecuritySectors()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var lines = new[] { "0-4095", "100000-104095" };
            var ok = true;
            try
            {
                SecuritySectors.ParseLines(lines, 0, 0, true);
            }
            catch
            {
                ok = false;
            }

            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Security",
                Status = ok ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = ok ? "ParseLines ok" : "ParseLines threw",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Security",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckXboxPrng()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var prng = new XboxPrng(1);
            // SimulateSectors is void; just ensure it doesn't throw and we can generate via WriteSectors
            using var ms = new MemoryStream();
            prng.WriteSectors(ms, 1);
            var len = ms.Length;
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Prng",
                Status = len == 2048 ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = $"WriteSectors len={len}",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Prng",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static SubBattleResult CheckXisoOperations(string? iso)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (iso == null || !File.Exists(iso))
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Ops",
                    Status = BattleStatus.Skipped,
                    Detail = "No sample",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            using var fs = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var entries = XisoRanges.GetFileEntries(fs, 0);
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Ops",
                    Status = BattleStatus.Passed,
                    Detail = $"{entries.Count} file entries",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
            catch (EndOfStreamException)
            {
                // Old ISOs or empty root may throw; fallback to GetXisoRanges which is more robust
                fs.Seek(0, SeekOrigin.Begin);
                (var sys, var files) =
                    XisoRanges.GetXisoRanges(fs, 0, true);
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Ops",
                    Status = BattleStatus.Passed,
                    Detail = $"fallback ranges sys={sys.Count} files={files.Count}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Ops",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    /// <summary>
    /// Glob patterns exercised by the <c>Glob</c> advanced self-check.
    /// </summary>
    internal static readonly string[] Patterns = new[] { "**/*.iso" };

    private static SubBattleResult CheckGlobMatcher()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var m = new GlobMatcher(Patterns);
            var ok = m.IsMatch("a/b.iso") && !m.IsMatch("a/b.txt");
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Glob",
                Status = ok ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = ok ? "match ok" : "mismatch",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Glob",
                Status = BattleStatus.Failed,
                Detail = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }
    }

    private static string CaptureCSharpList(string isoPath)
    {
        var saveQuiet = Logger.Quiet;
        var saveOut = Logger.Out;
        var origOut = Console.Out;
        try
        {
            Logger.Quiet = false;
            using var sw = new StringWriter();
            // XisoReader.List writes via Logger.Out (captured at startup), NOT via
            // Console.Out, so Console.SetOut alone captures nothing — redirect both.
            Logger.Out = sw;
            Console.SetOut(sw);
            try
            {
                XisoReader.List(isoPath, false);
            }
            catch
            {
                // ignored
            }

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

    private sealed record ListEntry(string Path, bool IsDirectory, long Size);

    private static string NormalizeListPath(string p)
    {
        var n = p.Replace('\\', '/');
        if (n.Length == 0)
        {
            return n;
        }

        if (!n.StartsWith('/'))
        {
            n = "/" + n;
        }

        return n;
    }

    private static List<ListEntry>? TryGetStructuredEntries(string isoPath)
    {
        // BTL-014: preferred structured source — walk the TOC directly instead
        // of scraping `List` text. Directory sizes map to 0 to match list
        // output (`(0 bytes)` for dirs); the image root itself is not a row.
        try
        {
            var layout = XisoReader.GetSectorLayout(isoPath);
            var list = new List<ListEntry>(layout.Entries.Count);
            foreach (var e in layout.Entries)
            {
                if (string.Equals(e.Path, "/", StringComparison.Ordinal))
                {
                    continue;
                }

                list.Add(new ListEntry(NormalizeListPath(e.Path), e.IsDirectory,
                    e.IsDirectory ? 0L : e.FileSize));
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static List<ListEntry> ParseListOutput(string output)
    {
        // Fallback text parser for `List` output (TryGetStructuredEntries is
        // preferred). Both sides print one entry per line:
        //   \path\to\file.ext (12345 bytes)      files
        //   \path\to\dir\ (0 bytes)              directories (trailing slash)
        // NOTE (sector-compare limitation): neither side prints StartSector in
        // list mode, so parity compares path+size+kind only; layout differences
        // are covered by Rewrite/CISO hashes, not here.
        var entries = new List<ListEntry>();
        var lineRegex = new Regex(@"^\s*(?<path>.*?)\s*\((?<size>\d+) bytes\)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromSeconds(30));
        foreach (Match m in lineRegex.Matches(output))
        {
            var p = m.Groups["path"].Value.Trim();
            if (p.Length == 0)
                continue;
            if (!long.TryParse(m.Groups["size"].Value, CultureInfo.InvariantCulture, out var s))
                continue;
            var isDir = p.EndsWith('\\') || p.EndsWith('/');
            entries.Add(new ListEntry(NormalizeListPath(p.TrimEnd('\\', '/')), isDir, s));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private sealed record ListCmp(bool AllMatch, string Detail);

    private static ListCmp CompareLists(List<ListEntry> cs, List<ListEntry> exe)
    {
        var details = new List<string>();
        if (cs.Count != exe.Count) details.Add($"count C#={cs.Count} exe={exe.Count}");
        // BTL-008: ordinal (case-sensitive) keys. Entries differing only by
        // case stay distinct and surface as CASE-COLLISION mismatches instead
        // of collapsing (false pass) or throwing on duplicate keys.
        var csDict = BuildListMap(cs, details, "C#");
        var exeDict = BuildListMap(exe, details, "exe");
        int match = 0;
        int mis = details.Count;
        var exeFold = new HashSet<string>(exeDict.Keys, StringComparer.OrdinalIgnoreCase);
        var csFold = new HashSet<string>(csDict.Keys, StringComparer.OrdinalIgnoreCase);
        foreach ((var p, var ce) in csDict)
        {
            if (exeDict.TryGetValue(p, out var ee))
            {
                if (ce.IsDirectory == ee.IsDirectory && ce.Size == ee.Size)
                {
                    match++;
                }
                else
                {
                    mis++;
                    details.Add(
                        $"MISMATCH {p} C#:{(ce.IsDirectory ? "DIR" : ce.Size.ToString(CultureInfo.InvariantCulture))} exe:{(ee.IsDirectory ? "DIR" : ee.Size.ToString(CultureInfo.InvariantCulture))}");
                }
            }
            else
            {
                mis++;
                if (exeFold.Contains(p))
                    details.Add($"CASE-COLLISION ONLY C# {p} (case differs on exe side)");
                else
                    details.Add($"ONLY C# {p}");
            }
        }

        foreach (var p in exeDict.Keys)
        {
            if (csDict.ContainsKey(p))
                continue;
            if (csFold.Contains(p))
                continue; // already reported as CASE-COLLISION from the C# side
            mis++;
            details.Add($"ONLY exe {p}");
        }

        var all = mis == 0;
        if (all) details.Add($"{match} match \u2713");
        return new ListCmp(all, string.Join("\n", details));
    }

    private static Dictionary<string, ListEntry> BuildListMap(List<ListEntry> entries, List<string> details,
        string side)
    {
        var dict = new Dictionary<string, ListEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!dict.TryAdd(e.Path, e))
            {
                details.Add($"DUPLICATE {side} {e.Path}");
            }
        }

        return dict;
    }

    private sealed record DirCmp(bool AllMatch, string Detail);

    private static DirCmp CompareDirs(string csDir, string exeDir)
    {
        var details = new List<string>();
        int match = 0, mis = 0;
        // BTL-008: ordinal relative paths; case-only differences are distinct
        // entries reported as mismatches (see below), never collapsed.
        var csFiles = BuildFileMap(csDir, details, "C#", ref mis);
        var exeFiles = BuildFileMap(exeDir, details, "exe", ref mis);
        var exeFold = new HashSet<string>(exeFiles.Keys, StringComparer.OrdinalIgnoreCase);
        var csFold = new HashSet<string>(csFiles.Keys, StringComparer.OrdinalIgnoreCase);
        foreach ((var rel, var cs) in csFiles)
        {
            if (exeFiles.TryGetValue(rel, out var exe))
            {
                try
                {
                    var ch = HashUtil.ComputeSha256(cs);
                    var eh = HashUtil.ComputeSha256(exe);
                    if (string.Equals(ch, eh, StringComparison.Ordinal))
                    {
                        match++;
                        if (match <= 3) details.Add($"\u2713 {rel}");
                    }
                    else
                    {
                        mis++;
                        details.Add($"\u2717 SHA256 {rel}\n  C#:{ch}\n  exe:{eh}");
                    }
                }
                catch (Exception ex)
                {
                    mis++;
                    details.Add($"\u2717 hash {rel}: {ex.Message}");
                }
            }
            else
            {
                mis++;
                if (exeFold.Contains(rel))
                    details.Add($"CASE-COLLISION ONLY C# {rel} (case differs on exe side)");
                else
                    details.Add($"ONLY C# {rel}");
            }
        }

        foreach (var rel in exeFiles.Keys)
        {
            if (csFiles.ContainsKey(rel))
                continue;
            if (csFold.Contains(rel))
                continue; // already reported as CASE-COLLISION above
            mis++;
            details.Add($"ONLY exe {rel}");
        }

        if (match > 3) details.Insert(3, $"... ({match - 3} more)");
        var all = mis == 0;
        if (all)
        {
            details.Clear();
            details.Add($"{match} files SHA256 match \u2713");
        }

        return new DirCmp(all, string.Join("\n", details));
    }

    private static Dictionary<string, string> BuildFileMap(string root, List<string> details, string side, ref int mis)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var full in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full);
            if (!dict.TryAdd(rel, full))
            {
                mis++;
                details.Add($"DUPLICATE {side} {rel}");
            }
        }

        return dict;
    }

    private static int CountFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string CreateTempDir(string name)
    {
        // BTL-021: full 32-char GUID per temp root (8-char prefixes collide across
        // parallel harnesses sharing %TEMP%\XISOSharpBattle) with a create-retry so
        // a rare collision retries with a fresh GUID instead of reusing a foreign dir.
        for (var attempt = 0;; attempt++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "XISOSharpBattle", Guid.NewGuid().ToString("N"),
                name);
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (IOException) when (attempt < 2)
            {
                // Possible GUID collision or transient failure — retry fresh.
            }
        }
    }

    private static void DeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // ignored
        }
    }
}
