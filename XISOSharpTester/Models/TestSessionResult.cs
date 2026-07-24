namespace XISOSharpTester.Models;

/// <summary>
/// Contains the aggregated results of an entire test session,
/// including per-file breakdowns and summary counts for all
/// sub-tests executed across all files.
/// </summary>
public class TestSessionResult
{
    /// <summary>
    /// Gets or sets the list of individual per-file test results
    /// that make up this session.
    /// </summary>
    public IList<PerFileResult> FileResults { get; set; } = [];

    /// <summary>
    /// Gets the total number of files that were tested.
    /// </summary>
    public int TotalFiles => FileResults.Count;

    /// <summary>
    /// Gets the number of files for which all sub-tests passed.
    /// </summary>
    public int PassedFiles => FileResults.Count(static r => r.AllPassed);

    /// <summary>
    /// Gets the number of files with at least one failed sub-test
    /// or no passing sub-tests at all.
    /// </summary>
    public int FailedFiles => FileResults.Count(static r => r.Failed > 0 || r.Passed == 0);

    /// <summary>
    /// Gets the number of files that were fully skipped (no passing
    /// or failing sub-tests).
    /// </summary>
    public int SkippedFiles => FileResults.Count(static r => r is { Skipped: > 0, Passed: 0, Failed: 0 });

    /// <summary>
    /// Gets the total number of sub-tests that were not skipped
    /// across all files.
    /// </summary>
    public int TotalSubTests => FileResults.Sum(static r => r.SubTests.Count(static t => t.Status != TestStatus.Skipped));

    /// <summary>
    /// Gets the total number of passing sub-tests across all files.
    /// </summary>
    public int PassedSubTests => FileResults.Sum(static r => r.Passed);

    /// <summary>
    /// Gets the total number of failing sub-tests across all files.
    /// </summary>
    public int FailedSubTests => FileResults.Sum(static r => r.Failed);

    /// <summary>
    /// Gets the total number of skipped sub-tests across all files.
    /// </summary>
    public int SkippedSubTests => FileResults.Sum(static r => r.Skipped);

    /// <summary>
    /// Gets the total elapsed wall-clock time across all per-file
    /// results, in seconds.
    /// </summary>
    public double TotalElapsedSeconds => FileResults.Sum(static r => r.ElapsedSeconds);
}
