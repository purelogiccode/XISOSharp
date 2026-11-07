using XISOSharp.TestDataGenerator;

namespace XISOSharp.BattleTests;

/// <summary>
/// Builds the deterministic, copyright-free corpus used by the CI battle job.
/// Delegates to <see cref="PublicCorpusWriter"/> so the battle harness and the
/// public corpus share one generator (DOC-02). The returned path contains the
/// XISO base images; BattleTests still exercises those images against the
/// checked-in reference executables.
/// </summary>
internal static class SyntheticCorpus
{
    /// <summary>
    /// Creates the corpus under <paramref name="root"/> and returns the
    /// directory holding the base <c>*.iso</c> files.
    /// </summary>
    /// <param name="cli">Resolved XISOSharp CLI process wrapper (unused; the shared writer uses the library).</param>
    /// <param name="root">Corpus root (created when missing).</param>
    /// <returns>The corpus root containing the generated <c>*.iso</c> files.</returns>
    internal static string Create(ToolProcess cli, string root)
    {
        ArgumentNullException.ThrowIfNull(cli);
        Directory.CreateDirectory(root);
        IReadOnlyList<string> actions = PublicCorpusWriter.EnsureCorpus(root, force: true);
        foreach (string action in actions)
        {
            Console.WriteLine($"[corpus] {action}");
        }

        return Path.Combine(root, "xiso");
    }
}
