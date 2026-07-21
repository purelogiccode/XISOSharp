namespace ExtractXisoTester.Models;

public class TestConfiguration
{
    public string ExtractXisoExePath { get; set; } = string.Empty;

    public List<XisoFileEntry> Files { get; set; } = [];
}
