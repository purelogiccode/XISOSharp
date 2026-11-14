#if NET11_0_OR_GREATER
using System.Text;
using XISOSharp.Cli;
using XISOSharp.Gui.Services;
using XISOSharp.Gui.ViewModels;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// GUI-01/GUI-02 acceptance: the Xbox 360 tab's argv builders drive the same
/// CLI verbs the GUI runs, and synthetic GOD/STFS inputs list, extract, title-
/// inspect and audit successfully. Extraction is compared byte-for-byte against
/// the equivalent direct CLI invocation.
/// </summary>
[Collection("Sequential")]
public sealed class GuiXbox360Tests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void GodContainer_GuiExtractArgs_MatchDirectCli()
    {
        string work = CreateTempDir();
        string source = Path.Combine(work, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "GOD GUI test\n");
        File.WriteAllText(Path.Combine(source, "sub", "deep.txt"), "deep\n");
        string iso = Path.Combine(work, "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string god = SyntheticGodContainer.CreateFromIso(iso, Path.Combine(work, "4D5307E6"),
            titleName: "GUI GOD", titleId: 0x4D5307E6);

        string guiOut = Path.Combine(work, "gui-out");
        string cliOut = Path.Combine(work, "cli-out");
        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
                CliCommands.Extract([god], guiOut, overwrite: true, jobs: 2)));
            Assert.Equal(0, Program.Main(["-x", "-y", "-d", cliOut, god]));
            Assert.Contains("2 files", capture.Output, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(Snapshot(guiOut), Snapshot(cliOut));
        Assert.Equal("GOD GUI test\n", File.ReadAllText(Path.Combine(guiOut, "readme.txt")));
        Assert.Equal("deep\n", File.ReadAllText(Path.Combine(guiOut, "sub", "deep.txt")));
    }

    [Fact]
    public void StfsPackage_GuiListExtractAuditAndTitle()
    {
        string work = CreateTempDir();
        string package = Path.Combine(work, "xbla-package");
        SyntheticStfsPackage.Create(package, [
            new SyntheticStfsFile("readme.txt", Encoding.UTF8.GetBytes("package\n")),
            new SyntheticStfsFile("data/payload.bin", [1, 2, 3, 4, 5]),
        ], titleName: "GUI STFS", xblig: false);

        string outDir = Path.Combine(work, "out");
        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(CliCommands.List([package])));
        Assert.Equal(0, Program.Main(CliCommands.Extract([package], outDir, overwrite: true)));
        Assert.Equal(0, Program.Main(CliCommands.Audit(package)));
        Assert.Equal(0, Program.Main(CliCommands.TitleInfo(package, json: true)));

        Assert.Equal("package\n", File.ReadAllText(Path.Combine(outDir, "readme.txt")));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(outDir, "data", "payload.bin")));
        Assert.Contains("GUI STFS", capture.Output, StringComparison.Ordinal);
        Assert.Contains("PASS", capture.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xbla", capture.Output, StringComparison.OrdinalIgnoreCase);

        string titleJson = capture.Output[capture.Output.IndexOf('{', StringComparison.Ordinal)..];
        Assert.Contains("Title: GUI STFS", MainViewModel.FormatTitleInfo(titleJson), StringComparison.Ordinal);
    }

    [Fact]
    public void GodTitleInfo_FormatsInTheGuiPanel()
    {
        string work = CreateTempDir();
        string source = Path.Combine(work, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "title\n");
        string iso = Path.Combine(work, "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        string god = SyntheticGodContainer.CreateFromIso(iso, Path.Combine(work, "4D5307E6"),
            titleName: "Panel Title", titleId: 0x12345678, discNumber: 1, discCount: 2);

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(CliCommands.TitleInfo(god, json: true)));
        string formatted = MainViewModel.FormatTitleInfo(capture.Output);
        Assert.Contains("Kind: god", formatted, StringComparison.Ordinal);
        Assert.Contains("Title: Panel Title", formatted, StringComparison.Ordinal);
        Assert.Contains("Title ID: 0x12345678", formatted, StringComparison.Ordinal);
        Assert.Contains("Disc count: 2", formatted, StringComparison.Ordinal);
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_gui360_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(file)));
        }

        return files;
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _origOut = Logger.Out;
        private readonly TextWriter _origErr = Logger.Error;
        private readonly bool _origQuiet = Logger.Quiet;
        private readonly bool _origRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        internal LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _err;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        internal string Output => _out.ToString() + _err;

        public void Dispose()
        {
            Logger.Out = _origOut;
            Logger.Error = _origErr;
            Logger.Quiet = _origQuiet;
            Logger.RealQuiet = _origRealQuiet;
            _out.Dispose();
            _err.Dispose();
        }
    }
}
#endif
