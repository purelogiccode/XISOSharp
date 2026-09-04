namespace XISOSharp.Gui.Services;

using Serilog;
using Logging;

/// <summary>
/// Headless verification for <c>--self-test</c>: asserts every
/// <see cref="CliCommands"/> builder emits the exact argv the CLI parsers
/// expect (flags before positionals, explicit <c>-y</c>/<c>-n</c>).
/// With a CLI path argument it additionally runs the real CLI <c>-v</c>
/// through <see cref="CliRunner"/>, i.e. the exact spawn/stream path the
/// GUI uses for every operation.
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// Asserts every <see cref="CliCommands"/> builder emits the expected argv and,
    /// with <paramref name="e2ECliPath"/>, runs the real CLI <c>-v</c> via <see cref="CliRunner"/>.
    /// </summary>
    /// <param name="log">Sink for PASS/FAIL lines.</param>
    /// <param name="e2ECliPath">Optional CLI path for the end-to-end runner check.</param>
    /// <returns>0 when all checks pass; otherwise 1.</returns>
    internal static int Run(Action<string> log, string? e2ECliPath = null)
    {
        var failures = 0;
        try
        {
            Log.Information("GUI self-test started");

        Check("extract", CliCommands.Extract(["a.iso"], "out", overwrite: false),
            ["-d", "out", "-x", "a.iso", "-n"]);
        Check("extract-nodest", CliCommands.Extract(["a.iso", "b.cso"], null, overwrite: true),
            ["-x", "a.iso", "b.cso", "-y"]);
        Check("list", CliCommands.List(["a.iso"]), ["-l", "a.iso"]);
        Check("tree", CliCommands.Tree(["a.iso"]), ["-t", "a.iso"]);
        Check("info", CliCommands.Info("a.iso", "/default.xbe"), ["-i", "a.iso", "/default.xbe"]);
        Check("unpack", CliCommands.Unpack("a.iso", "out"), ["--unpack", "a.iso", "out"]);
        Check("copy-out", CliCommands.CopyOut("a.iso", "/f.txt", "f.txt"),
            ["--copy-out", "a.iso", "/f.txt", "f.txt"]);
        Check("create",
            CliCommands.Create("src", "game", ["*.tmp"], skipSystemUpdate: true, disableXbePatch: false,
                overwrite: true),
            ["-c", "src", "game", "-X", "*.tmp", "-s", "-y"]);
        Check("rewrite", CliCommands.Rewrite(["a.iso"], "b.iso", null, deleteOld: true, disableXbePatch: false,
                validate: true, validateChecksums: false, validateStrict: true, validateReport: "r.json",
                overwrite: false),
            [
                "-r", "-o", "b.iso", "-D", "--validate", "--validate-strict", "--validate-report", "r.json", "a.iso",
                "-n"
            ]);
        Check("wipe", CliCommands.Wipe("a.iso", null, overwrite: true), ["--wipe", "a.iso", "-y"]);
        Check("trim", CliCommands.Trim("a.iso", "t.iso", overwrite: false), ["--trim", "a.iso", "t.iso", "-n"]);
        Check("rebuild", CliCommands.Rebuild(["x.iso", "v.iso"], "r.iso", "s.txt", overwrite: true),
            ["rebuild", "x.iso", "v.iso", "-o", "r.iso", "--security-sectors", "s.txt", "-y"]);
        Check("compress", CliCommands.Compress("a.iso", null, 9, 2, "0", overwrite: false),
            ["compress", "--ciso-level", "9", "--ciso-version", "2", "--ciso-split", "0", "a.iso", "-n"]);
        Check("decompress", CliCommands.Decompress("a.cso", "a.iso", overwrite: true),
            ["decompress", "a.cso", "a.iso", "-y"]);
        Check("validate", CliCommands.Validate("a.iso", "b.iso", checksums: true, report: "r.json"),
            ["validate", "--validate-checksums", "--validate-report", "r.json", "a.iso", "b.iso"]);
        Check("checksum", CliCommands.Checksum(["a.iso"], silent: false), ["checksum", "a.iso"]);
        Check("batch", CliCommands.Batch("dir", recursive: true, "-r", "out", overwrite: false),
            ["--batch", "dir", "--batch-recursive", "-d", "out", "-r", "-n"]);

        log(failures == 0 ? "SELF-TEST: all passed" : $"SELF-TEST: {failures} failure(s)");
        if (failures != 0)
        {
            Log.Warning("GUI self-test: {Failures} failure(s)", failures);
            BugReporter.ReportWarning($"GUI self-test: {failures} failure(s)");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(e2ECliPath))
        {
            try
            {
                var lines = new List<string>();
                var exit = CliRunner.RunAsync(e2ECliPath, ["-v"], lines.Add, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var ok = exit == 0 && lines.Count > 0;
                log($"{(ok ? "PASS" : "FAIL")} runner-e2e (-v via CliRunner, exit {exit}, {lines.Count} line(s))");
                if (!ok)
                {
                    Log.Warning("GUI self-test runner-e2e failed (exit {Exit})", exit);
                    BugReporter.ReportWarning($"GUI self-test runner-e2e failed (exit {exit})");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GUI self-test runner-e2e failed");
                BugReporter.ReportException(ex, "GUI self-test runner-e2e failed");
                log($"FAIL runner-e2e ({ex.Message})");
                return 1;
            }
        }

        Log.Information("GUI self-test passed");
        return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GUI self-test crashed");
            BugReporter.ReportException(ex, "GUI self-test crashed");
            try { log($"SELF-TEST: crashed ({ex.Message})"); }
            catch
            {
                // ignored
            }

            return 1;
        }

        void Check(string name, string[] actual, string[] expected)
        {
            var ok = actual.Length == expected.Length;
            if (ok)
            {
                for (var i = 0; i < actual.Length; i++)
                {
                    if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            log($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
            {
                log($"  expected: {string.Join(" ", expected)}");
                log($"  actual:   {string.Join(" ", actual)}");
                try
                {
                    Log.Warning("Self-test FAIL {Name}: expected [{Expected}] actual [{Actual}]",
                        name, string.Join(" ", expected), string.Join(" ", actual));
                }
                catch
                {
                    // Logging must never break the self-test.
                }

                failures++;
            }
        }
    }
}