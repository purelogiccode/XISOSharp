namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Entry point for the TestData fixture generator. Parses an optional target root
/// plus <c>--force</c>/<c>--help</c> and restores the deterministic source tree and ISO.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Restores the TestData fixture at the requested root and reports each action taken.
    /// </summary>
    /// <param name="args">Optional root path, <c>--force</c>/<c>-f</c>, and <c>--help</c>/<c>-h</c>.</param>
    /// <returns>0 on success; 1 when fixture creation fails.</returns>
    private static int Main(string[] args)
    {
        string? root = null;
        bool force = false;
        bool corpus = false;

        foreach (string arg in args)
        {
            if (arg is "--force" or "-f")
            {
                force = true;
            }
            else if (arg is "--corpus")
            {
                corpus = true;
            }
            else if (arg is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }
            else
            {
                root = arg;
            }
        }

        root ??= corpus
            ? Path.Combine(TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory), "corpus")
            : TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory);

        try
        {
            IReadOnlyList<string> actions = corpus
                ? PublicCorpusWriter.EnsureCorpus(root, force)
                : TestDataWriter.EnsureTestData(root, force);
            foreach (string action in actions)
            {
                Console.WriteLine(action);
            }

            Console.WriteLine($"{(corpus ? "Corpus" : "TestData")} ready at '{root}'");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Prints generator usage, including the fixture layout and supported flags.
    /// </summary>
    private static void PrintUsage() =>
        Console.WriteLine("""
                          Usage: XISOSharp.TestDataGenerator [path] [--corpus] [--force]

                          Restores the TestData fixture used by the XISOSharp test suite:
                            source\file1.txt, source\file2.txt, source\binary.bin, source\test.xbe,
                            source\subdir\subfile.txt, source\subdir\nested\deep.txt
                            output\source.iso (rebuilt from source on every run)

                          With --corpus, generates the public synthetic corpus instead:
                            xiso, split ISO, CISO, CCI, ZAR, GOD/SVOD, XBLA/XBLIG STFS,
                            XBE/XEX fixtures and corrupt variants, plus corpus-manifest.json.
                            All content is generated; no game data is included.

                            path     Target root directory
                                     (TestData default: <repo root>\TestData;
                                      corpus default: <repo root>\TestData\corpus)
                            --force  Regenerate the fixture/corpus even when current
                          """);
}
