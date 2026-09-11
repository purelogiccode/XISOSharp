namespace XISOSharp.TestDataGenerator;

/// <summary>
/// Single owner of TestData/solution-root path resolution (BUG-TEST-006).
/// Centralizes the fragile <c>Combine(BaseDirectory, "..", "..", "..", "..", "TestData")</c>
/// literal previously duplicated across <c>TestDataFixture</c>,
/// <c>TestDataGenerator/Program</c>, and every integration test, so TFM/output-depth
/// or publish-layout changes are fixed in one place.
/// </summary>
public static class TestDataLocator
{
    /// <summary>
    /// Resolves the repo <c>TestData</c> root by walking up from
    /// <c>AppContext.BaseDirectory</c> until a directory containing
    /// <c>CSharp_XISOSharp.sln</c> (or a <c>TestData</c> child) is found.
    /// Falls back to the legacy 4-level parent traversal for exotic layouts.
    /// </summary>
    /// <param name="baseDirectory">Base directory to walk up from (defaults to the app base).</param>
    /// <returns>Full path of the <c>TestData</c> root (created on demand by the writer).</returns>
    public static string GetTestDataRoot(string? baseDirectory = null)
    {
        string start = baseDirectory ?? AppContext.BaseDirectory;
        string? dir = Path.GetFullPath(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return Path.Combine(dir, "TestData");
            }

            dir = Path.GetDirectoryName(dir);
        }

        // Legacy fallback: <assembly>/bin/<cfg>/<tfm> is four levels below the repo root.
        return Path.GetFullPath(Path.Combine(
            baseDirectory ?? AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));
    }

    /// <summary>Resolves the <c>TestData/source</c> fixture source tree.</summary>
    public static string GetSourceDir(string? testDataRoot = null) =>
        Path.Combine(testDataRoot ?? GetTestDataRoot(), "source");

    /// <summary>Resolves the prebuilt <c>TestData/output/source.iso</c> fixture image.</summary>
    public static string GetOutputIsoPath(string? testDataRoot = null) =>
        Path.Combine(testDataRoot ?? GetTestDataRoot(), "output", TestDataWriter.IsoFileName);

    /// <summary>
    /// Resolves the solution root by walking up to <c>CSharp_XISOSharp.sln</c>.
    /// Shared by oracle-path probes so tests do not duplicate the walk.
    /// </summary>
    /// <returns>Solution root, or <c>null</c> when not found.</returns>
    public static string? GetSolutionRoot(string? baseDirectory = null)
    {
        string? dir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
