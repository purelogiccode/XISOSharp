namespace XISOSharpTester.Models;

public class TestConfiguration
{
    public string XISOSharpExePath { get; set; } = string.Empty;

    public List<XisoFileEntry> Files { get; set; } = [];
}
