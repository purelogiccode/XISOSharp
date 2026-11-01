using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Phase 5 CLI batch conversion tests: <c>compress</c>, <c>decompress</c>,
/// <c>convert</c> and <c>split</c> accept <c>--batch</c>, naming templates,
/// <c>--skip-existing</c>, <c>--continue-on-error</c> and <c>--jobs</c>.
/// </summary>
[Collection("Sequential")]
public sealed class CliBatchConvertTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void CompressBatch_Ciso_ConvertsEveryIso()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string isoA = CreateIso(work, isoDir, "a");
        string isoB = CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--batch", isoDir, "--ciso-split", "0", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.cso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "b.cso")), capture.Output);
            Assert.Contains("2 converted", capture.Output, StringComparison.Ordinal);
        }

        // Round-trip one output to prove the batch did not just touch names.
        string outIso = Path.Combine(work, "roundtrip.iso");
        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["decompress", Path.Combine(isoDir, "a.cso"), outIso, "-y"]));
            Assert.True(File.Exists(outIso), capture.Output);
        }

        Assert.Equal(File.ReadAllBytes(isoA), File.ReadAllBytes(outIso));
        Assert.NotEqual(File.ReadAllBytes(isoA), File.ReadAllBytes(isoB));
    }

    [Fact]
    public void CompressBatch_Cci_ConvertsEveryIso()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
                ["compress", "--batch", isoDir, "--format", "cci", "--cci-split", "0", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.cci")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "b.cci")), capture.Output);
        }
    }

    [Fact]
    public void CompressBatch_SkipExisting_LeavesExistingOutputsAlone()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--batch", isoDir, "--ciso-split", "0", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.cso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "b.cso")), capture.Output);
        }

        // Replace a.cso with a sentinel and remove b.cso: a resumed run must
        // leave the sentinel untouched and recreate only the missing output.
        string csoA = Path.Combine(isoDir, "a.cso");
        File.WriteAllText(csoA, "sentinel");
        File.Delete(Path.Combine(isoDir, "b.cso"));

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
                ["compress", "--batch", isoDir, "--ciso-split", "0", "--skip-existing", "-y"]));
            Assert.Contains("1 converted, 1 skipped", capture.Output, StringComparison.Ordinal);
        }

        Assert.Equal("sentinel", File.ReadAllText(csoA));
        Assert.True(File.Exists(Path.Combine(isoDir, "b.cso")));
    }

    [Fact]
    public void DecompressBatch_ConvertsCsoAndCci()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string csoDir = Path.Combine(work, "cso");
        string cciDir = Path.Combine(work, "cci");
        string isoA = CreateIso(work, csoDir, "a");
        string isoB = CreateIso(work, cciDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--batch", csoDir, "--ciso-split", "0", "-y"]));
            Assert.Equal(0, Program.Main(
                ["compress", "--batch", cciDir, "--format", "cci", "--cci-split", "0", "-y"]));
            Assert.True(File.Exists(Path.Combine(csoDir, "a.cso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(cciDir, "b.cci")), capture.Output);
        }

        File.Delete(isoA);
        File.Delete(isoB);

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["decompress", "--batch", csoDir, "-y"]));
            Assert.Equal(0, Program.Main(["decompress", "--batch", cciDir, "-y"]));
            Assert.Contains("1 converted", capture.Output, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(isoA));
        Assert.True(File.Exists(isoB));
        Assert.Equal(File.ReadAllBytes(Path.Combine(csoDir, "a.iso")), File.ReadAllBytes(isoA));
    }

    [Fact]
    public void CompressBatch_OutputNameTemplate_UsesNameExtAndFormat()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
            [
                "compress", "--batch", isoDir, "--ciso-split", "0",
                "--output-name", "{name}.from-{ext}.{format}", "-y"
            ]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.from-iso.cso")), capture.Output);
        }
    }

    [Fact]
    public void CompressBatch_OutputNameTemplate_CreatesSubdirectory()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
            [
                "compress", "--batch", isoDir, "--ciso-split", "0",
                "--output-name", "out/{name}.{format}", "-y"
            ]));
            Assert.True(File.Exists(Path.Combine(isoDir, "out", "a.cso")), capture.Output);
        }
    }

    [Fact]
    public void CompressBatch_UnknownTemplateToken_IsRejected()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");

        using LogCapture capture = new();
        int rc = Program.Main(["compress", "--batch", isoDir, "--output-name", "{game}.cso", "-y"]);

        Assert.Equal(1, rc);
        Assert.Contains("unknown placeholder", capture.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(isoDir, "a.cso")));
    }

    [Fact]
    public void CompressBatch_DuplicateOutputs_AreRejectedBeforeWriting()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string iso = CreateIso(work, isoDir, "a");
        File.Copy(iso, Path.Combine(isoDir, "a.xiso"));

        using LogCapture capture = new();
        int rc = Program.Main(["compress", "--batch", isoDir, "--ciso-split", "0", "-y"]);

        Assert.Equal(1, rc);
        Assert.Contains("collide", capture.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(isoDir, "a.cso")));
    }

    [Fact]
    public void CompressBatch_Recursive_FindsNestedImages()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string nested = Path.Combine(work, "nested", "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, nested, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
                ["compress", "--batch", work, "--batch-recursive", "--ciso-split", "0", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.cso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(nested, "b.cso")), capture.Output);
        }
    }

    [Fact]
    public void CompressBatch_Jobs_ConvertsEveryIso()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        for (int i = 0; i < 3; i++)
            CreateIso(work, isoDir, $"game{i}");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(
                ["compress", "--batch", isoDir, "--ciso-split", "0", "--jobs", "2", "-Q", "-y"]));
        }

        for (int i = 0; i < 3; i++)
            Assert.True(File.Exists(Path.Combine(isoDir, $"game{i}.cso")));
    }

    [Fact]
    public void DecompressBatch_ContinueOnError_SkipsBadFileAndKeepsGoing()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string csoDir = Path.Combine(work, "cso");
        string isoA = CreateIso(work, isoDir, "a");
        string isoB = CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--batch", isoDir, "--ciso-split", "0", "-y"]));
            Directory.CreateDirectory(csoDir);
            File.Move(Path.Combine(isoDir, "a.cso"), Path.Combine(csoDir, "a.cso"));
            File.Move(Path.Combine(isoDir, "b.cso"), Path.Combine(csoDir, "b.cso"));
            Assert.True(File.Exists(isoA) && File.Exists(isoB), capture.Output);
        }

        // a.cso sorts first and is not a CISO: without continue-on-error the
        // run stops there; with it, b.iso is still produced.
        File.WriteAllText(Path.Combine(csoDir, "a.cso"), "not a ciso");
        File.Delete(isoA);
        File.Delete(isoB);
        string outA = Path.Combine(csoDir, "a.iso");
        string outB = Path.Combine(csoDir, "b.iso");

        using (LogCapture capture = new())
        {
            Assert.Equal(1, Program.Main(["decompress", "--batch", csoDir, "--continue-on-error", "-y"]));
            Assert.Contains("1 converted, 0 skipped, 1 failed", capture.Output, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(outA));
        Assert.True(File.Exists(outB));
    }

    [Fact]
    public void ConvertBatch_IsoToZarAndBack()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string iso = CreateIso(work, isoDir, "game");
        byte[] originalHash = XisoChecksum.ComputeImageChecksum(iso);

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["convert", "--batch", isoDir, "--format", "zar", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "game.zar")), capture.Output);
        }

        string zarDir = Path.Combine(work, "zar");
        Directory.CreateDirectory(zarDir);
        File.Move(Path.Combine(isoDir, "game.zar"), Path.Combine(zarDir, "game.zar"));
        File.Delete(iso);

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["convert", "--batch", zarDir, "--format", "iso", "-y"]));
            Assert.True(File.Exists(Path.Combine(zarDir, "game.iso")), capture.Output);
        }

        Assert.Equal(originalHash, XisoChecksum.ComputeImageChecksum(Path.Combine(zarDir, "game.iso")));
    }

    [Fact]
    public void SplitBatch_SplitsEveryIso()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["split", "--batch", isoDir, "--size", "half", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.1.iso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "a.2.iso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "b.1.iso")), capture.Output);
            Assert.True(File.Exists(Path.Combine(isoDir, "b.2.iso")), capture.Output);
        }
    }

    [Fact]
    public void SplitBatch_SkipExisting_LeavesExistingPartsAlone()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["split", "--batch", isoDir, "--size", "half", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.1.iso")), capture.Output);
        }

        string partA = Path.Combine(isoDir, "a.1.iso");
        File.WriteAllText(partA, "sentinel");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["split", "--batch", isoDir, "--size", "half", "--skip-existing", "-y"]));
            Assert.Contains("0 converted, 2 skipped", capture.Output, StringComparison.Ordinal);
        }

        Assert.Equal("sentinel", File.ReadAllText(partA));
    }

    [Fact]
    public void SplitBatch_SkipExisting_ResumesPartialSet()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        CreateIso(work, isoDir, "a");
        CreateIso(work, isoDir, "b");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["split", "--batch", isoDir, "--size", "half", "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "a.2.iso")), capture.Output);
        }

        // a is missing its second part, b is complete: only a is re-split.
        File.Delete(Path.Combine(isoDir, "a.2.iso"));

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["split", "--batch", isoDir, "--size", "half", "--skip-existing", "-y"]));
            Assert.Contains("1 converted, 1 skipped", capture.Output, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(isoDir, "a.1.iso")));
        Assert.True(File.Exists(Path.Combine(isoDir, "a.2.iso")));
    }

    [Fact]
    public void BatchNaming_StripsSplitMarkerAndExpandsTokens()
    {
        Assert.Equal("game", BatchConversion.BaseName(Path.Combine("dir", "game.iso")));
        Assert.Equal("game", BatchConversion.BaseName(Path.Combine("dir", "game.1.cso")));
        Assert.Equal("game", BatchConversion.BaseName(Path.Combine("dir", "game.1.cci")));
        Assert.Equal("game.2024", BatchConversion.BaseName(Path.Combine("dir", "game.2024.iso")));
        Assert.Equal("archive.1", BatchConversion.BaseName(Path.Combine("dir", "archive.1.zar")));
        Assert.Equal(1, BatchConversion.GetSplitPart(Path.Combine("dir", "game.1.iso")));
        Assert.Equal(2, BatchConversion.GetSplitPart(Path.Combine("dir", "game.2.cso")));
        Assert.Equal(0, BatchConversion.GetSplitPart(Path.Combine("dir", "archive.1.zar")));

        Assert.Equal("game.xiso.iso",
            BatchConversion.ExpandTemplate("{name}.xiso.{format}", "/tmp/game.1.cso", "iso"));
        Assert.Equal("game.from-cso.cci",
            BatchConversion.ExpandTemplate("{name}.from-{ext}.{format}", "/tmp/game.1.cso", "cci"));
        Assert.Null(BatchConversion.ValidateTemplate("{name}.{format}"));
        Assert.Contains("non-empty", BatchConversion.ValidateTemplate("  "), StringComparison.Ordinal);
        Assert.Contains("unknown placeholder",
            BatchConversion.ValidateTemplate("{name}.{bogus}"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertBatch_ToZar_SkipsSplitSets()
    {
        string work = CreateTempDir("xiso_cli_batch");
        string isoDir = Path.Combine(work, "iso");
        string iso = CreateIso(work, isoDir, "game");

        using (LogCapture splitCapture = new())
        {
            Assert.Equal(0, Program.Main(
                ["split", "--size", "half", "--output", Path.Combine(isoDir, "game"), iso, "-y"]));
            Assert.True(File.Exists(Path.Combine(isoDir, "game.1.iso")), splitCapture.Output);
        }

        File.Delete(iso);

        using (LogCapture capture = new())
        {
            int rc = Program.Main(["convert", "--batch", isoDir, "--format", "zar", "-y"]);
            Assert.Equal(1, rc);
            Assert.Contains("no unsplit .iso/.xiso files", capture.Output, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(isoDir, "game.zar")));
    }

    [Fact]
    public void ConvertBatch_FormatWithoutBatch_IsRejected()
    {
        using LogCapture capture = new();
        int rc = Program.Main(["convert", "--format", "zar", "a.iso", "a.zar"]);

        Assert.Equal(1, rc);
        Assert.Contains("--format requires --batch", capture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchFlags_OutsideBatch_AreRejected()
    {
        using LogCapture capture = new();
        Assert.Equal(1, Program.Main(["compress", "--skip-existing", "a.iso"]));
        Assert.Contains("--skip-existing requires --batch", capture.Output, StringComparison.Ordinal);

        using LogCapture capture2 = new();
        Assert.Equal(1, Program.Main(["decompress", "--jobs", "2", "a.cso"]));
        Assert.Contains("--jobs requires --batch", capture2.Output, StringComparison.Ordinal);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateIso(string work, string isoDir, string name)
    {
        string src = Path.Combine(work, "src_" + name);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "hello " + name);
        File.WriteAllText(Path.Combine(src, "data.bin"), new string(name[0], 4096));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "nested.txt"), "nested " + name);

        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null,
            out string? isoPath, name + ".iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
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

        internal string Output => _out.ToString() + _err.ToString();

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
