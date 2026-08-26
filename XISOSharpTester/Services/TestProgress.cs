namespace XISOSharpTester.Services;

/// <summary>
/// Reports the current progress of a test session, including
/// which file is being processed and the current test phase.
/// </summary>
/// <param name="CurrentFile">Name of the file currently being tested.</param>
/// <param name="FileIndex">One-based index of the current file within the total set.</param>
/// <param name="TotalFiles">Total number of files in the session.</param>
/// <param name="CurrentTest">Name of the sub-test currently running (e.g. "Verify", "List").</param>
/// <param name="StatusText">Human-readable status message describing the current operation.</param>
/// <param name="IsComplete"><c>true</c> when the entire session has finished; otherwise <c>false</c>.</param>
public record TestProgress(
    string CurrentFile,
    int FileIndex,
    int TotalFiles,
    string CurrentTest,
    string StatusText,
    bool IsComplete = false
);