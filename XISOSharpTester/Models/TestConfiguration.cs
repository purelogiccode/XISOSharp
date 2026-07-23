namespace XISOSharpTester.Models;

/// <summary>
/// Holds the configuration for a test session, including the path
/// to the extract-xiso executable and the list of XISO files to test.
/// </summary>
public class TestConfiguration
{
    /// <summary>
    /// Gets or sets the full path to the extract-xiso.exe binary
    /// used for comparison tests.
    /// </summary>
    public string XisoSharpExePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection of XISO file entries to include
    /// in the test run.
    /// </summary>
    public List<XisoFileEntry> Files { get; set; } = [];
}
