namespace XISOSharpTester.Models;

/// <summary>
/// Reports the current progress of a test session, including
/// the current file position and test phase.
/// </summary>
/// <param name="FileIndex">One-based index of the current file within the total set.</param>
/// <param name="TotalFiles">Total number of files in the session.</param>
/// <param name="CurrentTest">Name of the sub-test currently running (e.g. "Verify", "List").</param>
/// <param name="StatusText">Human-readable status message describing the current operation.</param>
public record TestProgress(
    int FileIndex,
    int TotalFiles,
    string CurrentTest,
    string StatusText
);
