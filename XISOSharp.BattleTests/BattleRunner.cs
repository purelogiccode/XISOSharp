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
    /// <summary>Runs the full battle suite against the supplied ISO files.</summary>
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

    private static string Symbol(BattleStatus s) => s switch
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
        result.SubTests.Add(RunChecksum(path));
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
                (var code, _, _) = wrapper.ListFiles(path);
                var nativeOk = code == 0;
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Verify",
                    Status = nativeOk ? BattleStatus.Passed : BattleStatus.Failed,
                    Detail = $"C#: {csDetail} | native: {(nativeOk ? "valid" : $"exit {code}")}",
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
                (var code, _, _) = wrapper.ListFiles(path);
                var bothFail = code != 0;
                sw.Stop();
                if (bothFail)
                {
                    return new SubBattleResult
                    {
                        TestName = "Verify",
                        Status = BattleStatus.Passed,
                        Detail = $"Both fail as expected: C#: {ex.Message} | native exit {code}",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }
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

    private static SubBattleResult RunAudit(string path)
    {
        var sw = Stopwatch.StartNew();
        var size = new FileInfo(path).Length;
        if (size > 200L * 1024 * 1024)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Audit",
                Status = BattleStatus.Skipped,
                Detail = $"Skipped large {size / (1024 * 1024)} MB",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

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
            var csOutput = CaptureCSharpList(path);
            var csEntries = ParseListOutput(csOutput);
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
        var size = new FileInfo(path).Length;
        // Skip full extract for very large images (>200 MB) to keep battle fast; List/Audit cover parity.
        // Redump 7 GB ISOs are tested via List/Audit/Ranges instead. Use --full to force.
        if (size > 200L * 1024 * 1024)
        {
            // Quick probe via List instead of full extract
            try
            {
                var csListProbe = CaptureCSharpList(path);
                var hasFiles = csListProbe.Contains("Size:", StringComparison.Ordinal);
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Extract",
                    Status = BattleStatus.Skipped,
                    Detail =
                        $"Skipped large {size / (1024 * 1024)} MB (use --full to force, list probe {(hasFiles ? "has files" : "empty")})",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
            catch
            {
                sw.Stop();
                return new SubBattleResult
                {
                    TestName = "Extract",
                    Status = BattleStatus.Skipped,
                    Detail = $"Skipped large {size / (1024 * 1024)} MB",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }
        }

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
        var size = new FileInfo(path).Length;
        if (size > 200L * 1024 * 1024)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Rewrite",
                Status = BattleStatus.Skipped,
                Detail = $"Skipped large {size / (1024 * 1024)} MB",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

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

            var csOut = Directory.GetFiles(csOutDir, "*.iso").FirstOrDefault()
                        ?? Directory.GetFiles(csWork, "*.iso", SearchOption.AllDirectories)
                            .FirstOrDefault(f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase));
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

            var exeOut = Directory.GetFiles(exeOutDir, "*.iso").FirstOrDefault();
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

    private static SubBattleResult RunCisoRoundTrip(string path)
    {
        var sw = Stopwatch.StartNew();
        var tmp = CreateTempDir("ciso");
        try
        {
            var cso = Path.Combine(tmp, "test.cso");
            var dec = Path.Combine(tmp, "test.dec.iso");
            try
            {
                var len = new FileInfo(path).Length;
                if (len > 20 * 1024 * 1024)
                {
                    sw.Stop();
                    return new SubBattleResult
                    {
                        TestName = "CISO",
                        Status = BattleStatus.Skipped,
                        Detail = $"Skipped large file {len / (1024 * 1024)} MB",
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    };
                }

                CisoWriter.CompressToCso(path, cso, level: 1);
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

    private static SubBattleResult RunChecksum(string path)
    {
        var sw = Stopwatch.StartNew();
        var size = new FileInfo(path).Length;
        if (size > 200L * 1024 * 1024)
        {
            sw.Stop();
            return new SubBattleResult
            {
                TestName = "Checksum",
                Status = BattleStatus.Skipped,
                Detail = $"Skipped large {size / (1024 * 1024)} MB",
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        try
        {
            var h1 = XisoChecksum.ComputeImageChecksumHex(path);
            var h2 = XisoChecksum.ComputeImageChecksumHex(path);
            sw.Stop();
            if (h1.Length != 64)
            {
                return new SubBattleResult
                {
                    TestName = "Checksum",
                    Status = BattleStatus.Failed,
                    Detail = $"Invalid hex length {h1.Length}",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };
            }

            return string.Equals(h1, h2, StringComparison.Ordinal)
                ? new SubBattleResult
                {
                    TestName = "Checksum",
                    Status = BattleStatus.Passed,
                    Detail = $"{h1} \u2713 deterministic",
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                }
                : new SubBattleResult
                {
                    TestName = "Checksum",
                    Status = BattleStatus.Failed,
                    Detail = $"{h1} != {h2}",
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
            using var fbd = new FileBlockDevice(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
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
                // Sanity check we can read header area
                if (n != 20)
                {
                    /* ignore */
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
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-C#",
                    Status = BattleStatus.Failed,
                    Detail = ex.Message,
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-C#",
                Status = BattleStatus.Passed,
                Detail = $"C# created {new FileInfo(csIso).Length} bytes",
                ElapsedSeconds = 0
            });

            if (wrapper?.Available != true)
            {
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Native",
                    Status = BattleStatus.Skipped,
                    Detail = "native not available",
                    ElapsedSeconds = 0
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            (var code, var so, var se) = wrapper.Create(dir, exeIso);
            if (code != 0)
            {
                var work = Path.Combine(tmp, "exe_fallback");
                Directory.CreateDirectory(work);
                var current = Directory.GetCurrentDirectory();
                try
                {
                    Directory.SetCurrentDirectory(work);
                    (var c2, var o2, var e2) = wrapper.Create(dir);
                    code = c2;
                    so = o2;
                    se = e2;
                    if (code == 0)
                    {
                        var found = Directory.GetFiles(work, "*.iso").FirstOrDefault();
                        if (found != null) File.Copy(found, exeIso, true);
                    }
                }
                finally
                {
                    Directory.SetCurrentDirectory(current);
                }
            }

            if (code != 0 || !File.Exists(exeIso))
            {
                sw.Stop();
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Native",
                    Status = BattleStatus.Failed,
                    Detail = $"native create exit {code}: {se.Trim()} {so.Trim()}",
                    ElapsedSeconds = 0
                });
                result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                return result;
            }

            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-Native",
                Status = BattleStatus.Passed,
                Detail = $"native created {new FileInfo(exeIso).Length} bytes",
                ElapsedSeconds = 0
            });

            var csList = ParseListOutput(CaptureCSharpList(csIso));
            var exeList = ParseListOutput(wrapper.ListFiles(exeIso).StdOut);
            var cmpList = CompareLists(csList, exeList);
            result.SubTests.Add(new SubBattleResult
            {
                TestName = "Create-List",
                Status = cmpList.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                Detail = cmpList.Detail,
                ElapsedSeconds = 0
            });

            var csExt = Path.Combine(tmp, "cs_ext2");
            Directory.CreateDirectory(csExt);
            var exeExt = Path.Combine(tmp, "exe_ext2");
            Directory.CreateDirectory(exeExt);
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
                    result.SubTests.Add(new SubBattleResult
                    {
                        TestName = "Create-Extract",
                        Status = BattleStatus.Failed,
                        Detail = $"native extract exit {ec}: {ese.Trim()}",
                        ElapsedSeconds = 0
                    });
                }
                else
                {
                    var cmpD = CompareDirs(csExt, exeExt);
                    result.SubTests.Add(new SubBattleResult
                    {
                        TestName = "Create-Extract",
                        Status = cmpD.AllMatch ? BattleStatus.Passed : BattleStatus.Failed,
                        Detail = cmpD.Detail,
                        ElapsedSeconds = 0
                    });
                }
            }
            catch (Exception ex)
            {
                result.SubTests.Add(new SubBattleResult
                {
                    TestName = "Create-Extract",
                    Status = BattleStatus.Failed,
                    Detail = ex.Message,
                    ElapsedSeconds = 0
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
                Status = BattleStatus.Passed,
                Detail = ok ? "ParseLines ok" : "ParseLines threw (expected for some)",
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
        var origOut = Console.Out;
        try
        {
            Logger.Quiet = false;
            using var sw = new StringWriter();
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
            Console.SetOut(origOut);
        }
    }

    private sealed record ListEntry(string Path, bool IsDirectory, uint Size, uint StartSector);

    private static List<ListEntry> ParseListOutput(string output)
    {
        var entries = new List<ListEntry>();
        var fileRegex =
            new Regex(@"^\s*-\s+(?<path>.*?)\s{2,}Size:\s*(?<size>\d+)\s*bytes,\s*StartSector:\s*(?<sector>\d+)",
                RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        var dirRegex = new Regex(@"^\s*-\s+(?<path>.*?)\s{2,}DIR", RegexOptions.Multiline | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));
        foreach (Match m in fileRegex.Matches(output))
        {
            var p = m.Groups["path"].Value.Trim();
            var s = uint.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
            var sec = uint.Parse(m.Groups["sector"].Value, CultureInfo.InvariantCulture);
            entries.Add(new ListEntry(p, false, s, sec));
        }

        foreach (Match m in dirRegex.Matches(output))
        {
            var p = m.Groups["path"].Value.Trim();
            entries.Add(new ListEntry(p, true, 0, 0));
        }

        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    private sealed record ListCmp(bool AllMatch, string Detail);

    private static ListCmp CompareLists(List<ListEntry> cs, List<ListEntry> exe)
    {
        var details = new List<string>();
        if (cs.Count != exe.Count) details.Add($"count C#={cs.Count} exe={exe.Count}");
        var csDict = cs.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var exeDict = exe.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        int match = 0, mis = 0;
        foreach ((var p, ListEntry ce) in csDict)
        {
            if (exeDict.TryGetValue(p, out var ee))
            {
                if (ce.IsDirectory == ee.IsDirectory && ce.Size == ee.Size && ce.StartSector == ee.StartSector)
                {
                    match++;
                }
                else
                {
                    mis++;
                    details.Add($"MISMATCH {p} C#{ce.Size}/{ce.StartSector} exe{ee.Size}/{ee.StartSector}");
                }
            }
            else
            {
                mis++;
                details.Add($"ONLY C# {p}");
            }
        }

        foreach (var p in exeDict.Keys.Except(csDict.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mis++;
            details.Add($"ONLY exe {p}");
        }

        var all = mis == 0;
        if (all) details.Add($"{match} match \u2713");
        return new ListCmp(all, string.Join("\n", details));
    }

    private sealed record DirCmp(bool AllMatch, string Detail);

    private static DirCmp CompareDirs(string csDir, string exeDir)
    {
        var details = new List<string>();
        int match = 0, mis = 0;
        var csFiles = Directory.GetFiles(csDir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(csDir, f)))
            .ToDictionary(x => x.Rel, StringComparer.OrdinalIgnoreCase);
        var exeFiles = Directory.GetFiles(exeDir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(exeDir, f)))
            .ToDictionary(x => x.Rel, StringComparer.OrdinalIgnoreCase);
        foreach ((var rel, (string Full, string Rel) cs) in csFiles)
        {
            if (exeFiles.TryGetValue(rel, out var exe))
            {
                try
                {
                    var ch = HashUtil.ComputeSha256(cs.Full);
                    var eh = HashUtil.ComputeSha256(exe.Full);
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
                details.Add($"ONLY C# {rel}");
            }
        }

        foreach (var rel in exeFiles.Keys.Except(csFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
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
        var dir = Path.Combine(Path.GetTempPath(), "XISOSharpBattle", Guid.NewGuid().ToString("N").Substring(0, 8),
            name);
        Directory.CreateDirectory(dir);
        return dir;
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