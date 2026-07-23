namespace XISOSharpTester.Models;

public class PerFileResult
{
    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string FileSize { get; set; } = string.Empty;

    public List<SubTestResult> SubTests { get; set; } = [];

    public int Passed => SubTests.Count(t => t.Status == TestStatus.Passed);

    public int Failed => SubTests.Count(t => t.Status == TestStatus.Failed);

    public int Skipped => SubTests.Count(t => t.Status == TestStatus.Skipped);

    public bool AllPassed => Failed == 0 && Passed > 0;

    public double ElapsedSeconds { get; set; }
}

public class SubTestResult
{
    public string TestName { get; set; } = string.Empty;

    public TestStatus Status { get; set; }

    public string Detail { get; set; } = string.Empty;

    public double ElapsedSeconds { get; set; }
}

public enum TestStatus
{
    Passed,
    Failed,
    Skipped
}
