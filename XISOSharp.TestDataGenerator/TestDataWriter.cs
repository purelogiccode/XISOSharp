namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Generates the <c>TestData</c> fixture used by the integration-style tests:
/// a deterministic source tree plus a prebuilt XISO image.
/// Shared source between the <c>XISOSharp.TestDataGenerator</c> tool and the
/// <c>XISOSharp.Tests</c> module initializer (linked compile item), so both
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
    /// Ensures the TestData fixture exists at <paramref name="testDataRoot"/>,
    /// creating any missing source files and rebuilding the derived ISO.
    /// </summary>
    /// <param name="testDataRoot">Path of the TestData root directory (created if missing).</param>
    /// <param name="force">
    /// When <c>true</c>, rewrites existing source files with the canonical content;
    /// when <c>false</c>, existing files are left untouched.
    /// </param>
    /// <returns>Human-readable descriptions of every action taken.</returns>
    public static IReadOnlyList<string> EnsureTestData(string testDataRoot, bool force = false)
    {
        var actions = new List<string>();

        var sourceDir = Path.Combine(testDataRoot, "source");
        var outputDir = Path.Combine(testDataRoot, "output");
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
                for (var i = 4; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
            },
            Constants.SectorSize,
            force);

        // The ISO is a derived artifact: always rebuild so it matches the current writer
        var isoPath = Path.Combine(outputDir, IsoFileName);
        if (File.Exists(isoPath))
        {
            File.Delete(isoPath);
        }

        var wasQuiet = Logger.Quiet;
        var wasRealQuiet = Logger.RealQuiet;
        Logger.Quiet = true;
        Logger.RealQuiet = true;
        try
        {
            var rc = XisoWriter.CreateXiso(sourceDir, outputDir, null, null, out var createdIsoPath, null, null);
            if (rc != 0)
            {
                throw new InvalidOperationException($"TestData fixture: CreateXiso failed with code {rc}");
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

        return actions;
    }

    private static void WriteText(List<string> actions, string path, string content, bool force)
    {
        if (!force && File.Exists(path))
        {
            return;
        }

        File.WriteAllText(path, content);
        actions.Add($"{(force ? "rewrote" : "created")} '{path}'");
    }

    private static void WriteBinary(List<string> actions, string path, Action<byte[]> fill, int length, bool force)
    {
        if (!force && File.Exists(path))
        {
            return;
        }

        var data = new byte[length];
        fill(data);
        File.WriteAllBytes(path, data);
        actions.Add($"{(force ? "rewrote" : "created")} '{path}' ({length} bytes)");
    }
}