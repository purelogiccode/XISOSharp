using CCISharp;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// CLI contract tests for CCI compression, decompression and flag validation.
/// </summary>
[Collection("Sequential")]
public sealed class CliCciTests : IDisposable
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
    public void CompressAndDecompress_Cci_RoundTrips()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");
        string outIso = Path.Combine(work, "out.iso");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--format", "cci", "--cci-split", "0", iso, cci, "-y"]));
            Assert.True(File.Exists(cci), capture.Output);
        }

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["decompress", cci, outIso, "-y"]));
            Assert.True(File.Exists(outIso), capture.Output);
        }

        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(outIso));
    }

    [Fact]
    public void SplitCci_DecompressesFromFirstPart()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");
        string outIso = Path.Combine(work, "out.iso");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--format", "cci", "--cci-split", "200000", iso, cci, "-y"]));
            Assert.True(File.Exists(Path.Combine(work, "game.1.cci")), capture.Output);
        }

        using (new LogCapture())
        {
            Assert.Equal(0, Program.Main(["decompress", Path.Combine(work, "game.1.cci"), outIso, "-y"]));
        }

        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(outIso));
    }

    [Fact]
    public void Aliases_CciAndUncci_RoundTrip()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "alias.cci");
        string outIso = Path.Combine(work, "alias.iso");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["cci", "--cci-split", "0", iso, cci, "-y"]));
            Assert.True(File.Exists(cci), capture.Output);
        }

        using (new LogCapture())
        {
            Assert.Equal(0, Program.Main(["uncci", cci, outIso, "-y"]));
        }

        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(outIso));
    }

    [Fact]
    public void List_Cci_Succeeds()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["compress", "--format", "cci", "--cci-split", "0", iso, cci, "-y"]));
            Assert.True(File.Exists(cci), capture.Output);
        }

        using LogCapture list = new();
        Assert.Equal(0, Program.Main(["-l", cci]));
        Assert.Contains("a.txt", list.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CisoOnlyFlags_WithCciFormat_AreRejected()
    {
        using LogCapture capture = new();
        int rc = Program.Main(["compress", "--format", "cci", "--ciso-level", "9", "a.iso", "-n"]);
        Assert.Equal(1, rc);
        Assert.Contains("--ciso-level is only valid for CISO", capture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void CciLevel12_AndParallelJobs_RoundTrip()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "hc.cci");
        string outIso = Path.Combine(work, "hc.iso");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main([
                "compress", "--format", "cci", "--cci-level", "12", "--jobs", "4",
                "--cci-split", "0", iso, cci, "-y"]));
            Assert.True(File.Exists(cci), capture.Output);
        }

        using (new LogCapture())
        {
            Assert.Equal(0, Program.Main(["decompress", cci, outIso, "-y"]));
        }

        Assert.Equal(File.ReadAllBytes(iso), File.ReadAllBytes(outIso));
    }

    [Fact]
    public void CciParallelOutput_IsByteIdenticalToSequential()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateIso(work);
        string sequential = Path.Combine(work, "sequential.cci");
        string parallel = Path.Combine(work, "parallel.cci");

        using (new LogCapture())
        {
            Assert.Equal(0, Program.Main([
                "compress", "--format", "cci", "--cci-level", "12", "--jobs", "1",
                "--cci-split", "0", iso, sequential, "-y"]));
            Assert.Equal(0, Program.Main([
                "compress", "--format", "cci", "--cci-level", "12", "--jobs", "8",
                "--cci-split", "0", iso, parallel, "-y"]));
        }

        Assert.Equal(File.ReadAllBytes(sequential), File.ReadAllBytes(parallel));
    }

    [Fact]
    public void CciLevelOutsideRange_IsRejected()
    {
        using LogCapture capture = new();
        int rc = Program.Main(["compress", "--format", "cci", "--cci-level", "13", "a.iso", "-n"]);
        Assert.Equal(1, rc);
        Assert.Contains("--cci-level requires an integer 0..12", capture.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiDigitCciPart_IsDetectedAndExtracted()
    {
        string work = CreateTempDir("xiso_cli_cci");
        string iso = CreateLargeIso(work);
        byte[] raw = File.ReadAllBytes(iso);
        int totalSectors = raw.Length / CciFormat.SectorSize;
        Assert.True(totalSectors >= 10);
        int baseSectors = totalSectors / 10;
        int remainder = totalSectors % 10;
        int startSector = 0;

        for (int part = 1; part <= 10; part++)
        {
            int sectorCount = baseSectors + (part <= remainder ? 1 : 0);
            string chunk = Path.Combine(work, $"chunk-{part}.iso");
            using (FileStream output = File.Create(chunk))
            {
                output.Write(raw, startSector * CciFormat.SectorSize, sectorCount * CciFormat.SectorSize);
            }

            Assert.Equal(0, XisoCci.CompressToCci(
                chunk, Path.Combine(work, $"game.{part}.cci"), splitBytes: 0));
            startSector += sectorCount;
        }

        Assert.Equal(totalSectors, startSector);

        string part10 = Path.Combine(work, "game.10.cci");
        Assert.True(XisoImageSource.TryDetect(part10, out XisoImageSourceInfo? info));
        Assert.NotNull(info);
        Assert.Equal(XisoImageFormat.SplitCci, info.Format);
        Assert.Equal(10, info.Parts.Count);

        string extracted = Path.Combine(work, "from-part10");
        Assert.Equal(0, XisoReader.Extract(part10, extracted, llCompat: false));
        Assert.True(File.Exists(Path.Combine(extracted, "a.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(extracted, "a.txt")));
    }

    [Fact]
    public void CciOnlyFlags_WithCisoFormat_AreRejected()
    {
        using LogCapture capture = new();
        int rc = Program.Main(["compress", "--format", "cso", "--cci-split", "0", "a.iso", "-n"]);
        Assert.Equal(1, rc);
        Assert.Contains("--cci-split is only valid for CCI", capture.Output, StringComparison.Ordinal);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateIso(string work)
    {
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static string CreateLargeIso(string work)
    {
        string src = Path.Combine(work, "src-large");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

        byte[] payload = new byte[200_000];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31) + 7);
        }

        File.WriteAllBytes(Path.Combine(src, "big.bin"), payload);

        string isoDir = Path.Combine(work, "iso-large");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath, "game.iso", null));
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

        internal string Output => string.Concat(_out, _err);

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
