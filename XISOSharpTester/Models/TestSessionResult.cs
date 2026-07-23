namespace XISOSharpTester.Models;

public class TestSessionResult
{
    public List<PerFileResult> FileResults { get; set; } = [];

    public int TotalFiles => FileResults.Count;

    public int PassedFiles => FileResults.Count(r => r.AllPassed);

    public int FailedFiles => FileResults.Count(r => r.Failed > 0 || r.Passed == 0);

    public int SkippedFiles => FileResults.Count(r => r is { Skipped: > 0, Passed: 0, Failed: 0 });

    public int TotalSubTests => FileResults.Sum(r => r.SubTests.Count(t => t.Status != TestStatus.Skipped));

    public int PassedSubTests => FileResults.Sum(r => r.Passed);

    public int FailedSubTests => FileResults.Sum(r => r.Failed);

    public int SkippedSubTests => FileResults.Sum(r => r.Skipped);

    public double TotalElapsedSeconds => FileResults.Sum(r => r.ElapsedSeconds);
}
