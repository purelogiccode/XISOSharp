namespace XISOSharp.TestDataGenerator;

internal static class Program
{
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