namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Generates the <c>TestData</c> fixture used by the integration-style tests:
/// a deterministic source tree plus a prebuilt XISO image.
/// Single owner of the fixture logic: the <c>XISOSharp.TestDataGenerator</c> tool
/// and <c>XISOSharp.Tests</c> both consume it via a project reference, so both
/// always produce byte-identical fixtures.
/// </summary>
public static class TestDataWriter
{
    private const string File1Content = "XISOSharp test data file 1\n";
    private const string File2Content = "XISOSharp test data file 2 - different content\n";
    private const string SubfileContent = "Subdirectory file content\n";
    private const string DeepContent = "Deep nested file content\n";

    /// <summary>Name of the prebuilt ISO written under <c>output</c>.</summary>
    public const string IsoFileName = "source.iso";

    /// <summary>
    /// Cross-process fixture gate (BUG-TEST-007). Session-local so it does not
    /// collide across users; a fixed name is fine — worst case two different
    /// TestData roots merely serialize against each other.
    /// </summary>
    private const string MutexName = @"Local\XISOSharp.TestData.Fixture";

#if NET9_0_OR_GREATER
    private static readonly Lock Gate = new();
#else
    private static readonly object Gate = new();
#endif

    /// <summary>
    /// Ensures the TestData fixture exists at <paramref name="testDataRoot"/>,
    /// creating any missing source files and rebuilding the derived ISO.
    /// Sources are canonicalized on every call (BUG-TEST-005): an existing file
    /// whose bytes differ from the canonical content is rewritten even when
    /// <paramref name="force"/> is <c>false</c>, so a stale incremental tree
    /// converges to the same bytes as a clean checkout before the ISO — always
    /// rebuilt from those sources — is packed. The ISO is packed with
    /// <c>fileTime: 0</c> so its bytes are deterministic across runs/hosts
    /// (snapshot tests pack with fileTime 0 as well).
    /// </summary>
    /// <param name="testDataRoot">Path of the TestData root directory (created if missing).</param>
    /// <param name="force">
    /// When <c>true</c>, rewrites existing source files with the canonical content;
    /// when <c>false</c>, existing files are left untouched.
    /// </param>
    /// <returns>Human-readable descriptions of every action taken.</returns>
    public static IReadOnlyList<string> EnsureTestData(string testDataRoot, bool force = false)
    {
        // BUG-TEST-007: the static gate below only serializes threads inside one
        // process. Concurrent test hosts — parallel TFM runs (`dotnet test` builds
        // and runs net8/9/10), a Rider session overlapping a CLI run, and the
        // Generator tool — share one TestData/ tree, and a delete/rewrite landing
        // mid-rebuild in another process left the fixture half-written or threw
        // IOException out of the tests' ModuleInitializer (TypeInitializationException
        // failed every test in the host: 1274/1279 observed). Serialize across
        // processes with a named mutex, and ride out transient file locks
        // (Dropbox sync, AV scanners, indexer) with bounded retries.
        using Mutex mutex = new(false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromMinutes(5)))
            {
                throw new TimeoutException("Timed out waiting for the TestData fixture mutex (another host holds it).");
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous holder died mid-rebuild; we now own the mutex. The
            // canonicalize pass below repairs whatever partial state it left.
        }

        try
        {
            lock (Gate)
            {
                return EnsureTestDataCore(testDataRoot, force);
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static IReadOnlyList<string> EnsureTestDataCore(string testDataRoot, bool force)
    {
        List<string> actions = new();

        string sourceDir = Path.Combine(testDataRoot, "source");
        string outputDir = Path.Combine(testDataRoot, "output");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "subdir", "nested"));
        Directory.CreateDirectory(outputDir);

        WriteText(actions, Path.Combine(sourceDir, "file1.txt"), File1Content, force);
        WriteText(actions, Path.Combine(sourceDir, "file2.txt"), File2Content, force);
        WriteText(actions, Path.Combine(sourceDir, "subdir", "subfile.txt"), SubfileContent, force);
        WriteText(actions, Path.Combine(sourceDir, "subdir", "nested", "deep.txt"), DeepContent, force);

        WriteBinary(
            actions,
            Path.Combine(sourceDir, "binary.bin"),
            static data => new Random(42).NextBytes(data),
            Constants.HeaderOffset + Constants.SectorSize,
            force);

        // Fake XBE with plausible magic, padded to one sector
        WriteBinary(
            actions,
            Path.Combine(sourceDir, "test.xbe"),
            static data =>
            {
                "XBEH"u8.CopyTo(data);
                for (int i = 4; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
            },
            Constants.SectorSize,
            force);

        // The ISO is a derived artifact: always rebuild so it matches the current writer.
        // Sources above were canonicalized, so the rebuild is deterministic.
        {
            string isoPath = Path.Combine(outputDir, IsoFileName);

            // The rebuild writes/deletes several files in sequence; a transient
            // external lock (Dropbox sync, AV, indexer) landing anywhere in the
            // window must not fail the whole suite's ModuleInitializer.
            for (int attempt = 1;; attempt++)
            {
                try
                {
                    DeleteWithRetry(isoPath);
                    RebuildIso(actions, sourceDir, outputDir, isoPath);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200 * attempt);
                }
            }
        }

        return actions;
    }

    private static void RebuildIso(List<string> actions, string sourceDir, string outputDir, string isoPath)
    {
        bool wasQuiet = Logger.Quiet;
        bool wasRealQuiet = Logger.RealQuiet;
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        try
        {
            int rc = XisoWriter.CreateXiso(sourceDir, outputDir, null, null, out string? createdIsoPath, null, null,
                fileTime: 0UL);
            if (rc != 0)
            {
                throw new InvalidOperationException($"TestData fixture: CreateXiso failed with code {rc}");
            }

            string produced = createdIsoPath ?? isoPath;
            if (!string.Equals(produced, isoPath, StringComparison.OrdinalIgnoreCase) && File.Exists(produced))
            {
                // Tolerate writer naming drift: adopt whatever was produced.
                File.Move(produced, isoPath, overwrite: true);
                actions.Add($"renamed '{produced}' to '{isoPath}'");
            }

            if (!File.Exists(isoPath))
            {
                throw new InvalidOperationException(
                    $"TestData fixture: expected ISO at '{isoPath}' but writer produced '{createdIsoPath}'");
            }

            actions.Add($"rebuilt '{isoPath}'");
        }
        finally
        {
            Logger.Quiet = wasQuiet;
            Logger.RealQuiet = wasRealQuiet;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> with bounded retries: fixtures live under
    /// user-visible directories (this repo ships inside Dropbox), where sync
    /// engines and scanners hold freshly-written files open for a few hundred
    /// milliseconds. Unbounded blocking would be worse than a retry budget.
    /// </summary>
    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 1;; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void WriteText(List<string> actions, string path, string content, bool force)
    {
        if (!force && File.Exists(path))
        {
            // Heal stale incremental trees: keep the file only when its bytes
            // already match the canonical content (BUG-TEST-005).
            string existing;
            try
            {
                existing = File.ReadAllText(path);
            }
            catch
            {
                existing = string.Empty;
            }

            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }

            WriteAllTextWithRetry(path, content);
            actions.Add($"healed '{path}'");
            return;
        }

        WriteAllTextWithRetry(path, content);
        actions.Add($"{(force ? "rewrote" : "created")} '{path}'");
    }

    private static void WriteBinary(List<string> actions, string path, Action<byte[]> fill, int length, bool force)
    {
        byte[] data = new byte[length];
        fill(data);
        if (!force && File.Exists(path))
        {
            // Heal stale incremental trees (BUG-TEST-005): compare bytes.
            byte[] existing;
            try
            {
                existing = File.ReadAllBytes(path);
            }
            catch
            {
                existing = [];
            }

            if (existing.Length == data.Length && existing.AsSpan().SequenceEqual(data))
            {
                return;
            }

            WriteAllBytesWithRetry(path, data);
            actions.Add($"healed '{path}' ({length} bytes)");
            return;
        }

        WriteAllBytesWithRetry(path, data);
        actions.Add($"{(force ? "rewrote" : "created")} '{path}' ({length} bytes)");
    }

    private static void WriteAllTextWithRetry(string path, string content)
    {
        for (int attempt = 1;; attempt++)
        {
            try
            {
                File.WriteAllText(path, content);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void WriteAllBytesWithRetry(string path, byte[] data)
    {
        for (int attempt = 1;; attempt++)
        {
            try
            {
                File.WriteAllBytes(path, data);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
