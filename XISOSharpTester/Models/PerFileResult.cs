namespace XISOSharpTester.Models;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Contains the test results for a single XISO file, including
/// the aggregated counts and the collection of sub-test details.
/// </summary>
public class PerFileResult
{
    /// <summary>
    /// Gets or sets the display name of the file under test.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full path to the file under test.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable size of the file.
    /// </summary>
    public string FileSize { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection of individual sub-test results
    /// executed against this file.
    /// </summary>
    public IList<SubTestResult> SubTests { get; set; } = [];

    /// <summary>
    /// Gets the number of sub-tests that passed for this file.
    /// </summary>
    public int Passed => SubTests.Count(static t => t.Status == TestStatus.Passed);

    /// <summary>
    /// Gets the number of sub-tests that failed for this file.
    /// </summary>
    public int Failed => SubTests.Count(static t => t.Status == TestStatus.Failed);

    /// <summary>
    /// Gets the number of sub-tests that were skipped for this file.
    /// </summary>
    public int Skipped => SubTests.Count(static t => t.Status == TestStatus.Skipped);

    /// <summary>
    /// Gets whether all executed sub-tests passed (at least one
    /// passed and none failed).
    /// </summary>
    public bool AllPassed => Failed == 0 && Passed > 0;

    /// <summary>
    /// Gets or sets the total elapsed time for all tests on this
    /// file, in seconds.
    /// </summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>
/// Represents the outcome of a single sub-test (e.g. Verify,
/// List, Extract, Rewrite) for a specific XISO file.
/// </summary>
public class SubTestResult
{
    /// <summary>
    /// Gets or sets the display name of the sub-test (e.g. "Verify XISO").
    /// </summary>
    public string TestName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pass/fail/skip status of the sub-test.
    /// </summary>
    public TestStatus Status { get; set; }

    /// <summary>
    /// Gets or sets additional detail text describing the test outcome
    /// (e.g. hash comparisons, error messages).
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the elapsed time for this sub-test, in seconds.
    /// </summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>
/// Indicates the result status of a test or sub-test.
/// </summary>
public enum TestStatus
{
    /// <summary>The test completed successfully.</summary>
    Passed,

    /// <summary>The test completed with failures.</summary>
    Failed,

    /// <summary>The test was not executed (e.g. missing dependency).</summary>
    Skipped
}