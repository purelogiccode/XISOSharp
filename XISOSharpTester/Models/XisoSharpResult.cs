namespace XISOSharpTester.Models;

/// <summary>
/// Holds the result of a single extract-xiso process execution,
/// including exit code and captured standard output/error.
/// </summary>
public sealed class XisoSharpResult
{
    internal int ExitCode;
    internal string StdOut = null!;
    internal string StdErr = null!;
    internal string All => StdOut + "\n" + StdErr;
}
