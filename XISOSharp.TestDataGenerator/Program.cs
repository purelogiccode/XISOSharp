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
        var force = false;

        foreach (var arg in args)
        {
            if (arg is "--force" or "-f")
            {
                force = true;
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

        root ??= Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

        try
        {
            foreach (var action in TestDataWriter.EnsureTestData(root, force))
            {
                Console.WriteLine(action);
            }

            Console.WriteLine($"TestData ready at '{root}'");
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
    private static void PrintUsage()
    {
        Console.WriteLine("""
                          Usage: XISOSharp.TestDataGenerator [path] [--force]

                          Restores the TestData fixture used by the XISOSharp test suite:
                            source\file1.txt, source\file2.txt, source\binary.bin, source\test.xbe,
                            source\subdir\subfile.txt, source\subdir\nested\deep.txt
                            output\source.iso (rebuilt from source on every run)

                            path     Target TestData root directory
                                     (default: <repo root>\TestData)
                            --force  Rewrite existing source files with the canonical content
                          """);
    }
}