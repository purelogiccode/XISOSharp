namespace XISOSharpTester.Models;

public class TestConfiguration
{
    public string XisoSharpExePath { get; set; } = string.Empty;

    public List<XisoFileEntry> Files { get; set; } = [];
}
