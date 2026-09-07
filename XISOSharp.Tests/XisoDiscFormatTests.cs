using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="VolumeInfo.DiscFormat"/> — the friendly disc-layout
/// identity derived from the probe offset (<c>RAW</c>, <c>GLOBAL (XGD2)</c>,
/// <c>XGD3</c>, <c>XGD2 Hybrid</c>, <c>XGD1</c>), surfaced by <c>-i</c>.
/// </summary>
[Collection("Sequential")]
public class XisoDiscFormatTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly TextWriter _savedOut = Logger.Out;
    private readonly StringWriter _logCapture = new();

    public XisoDiscFormatTests()
    {
        Logger.Out = _logCapture;
    }

    public void Dispose()
    {
        Logger.Out = _savedOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Theory]
    [InlineData(0L, "RAW")]
    [InlineData(0x0FD90000L, "GLOBAL (XGD2)")]
    [InlineData(0x02080000L, "XGD3")]
    [InlineData(0x89D80000L, "XGD2 Hybrid")]
    [InlineData(0x18300000L, "XGD1")]
    [InlineData(0x12345678L, "Unknown")]
    public void DiscFormat_MapsKnownOffsets(long discLseek, string expected)
    {
        var vol = new VolumeInfo(true, 0, 0, discLseek, 0, 0);

        Assert.Equal(expected, vol.DiscFormat);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(0x02080000L)]
    public void DiscFormat_InvalidVolume_ReturnsUnknown(long discLseek)
    {
        // An invalid probe carries no layout identity even when the offset
        // happens to be zero or match a known layout.
        var vol = new VolumeInfo(false, 0, 0, discLseek, 0, 0);

        Assert.Equal("Unknown", vol.DiscFormat);
    }

    [Fact]
    public void DiscFormat_PackedIso_RoundTripsRaw()
    {
        var srcDir = CreateTempDir("xiso_discfmt_src");
        File.WriteAllText(Path.Combine(srcDir, "readme.txt"), "hello");

        var outputDir = CreateTempDir("xiso_discfmt_out");
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        var vol = XisoReader.GetVolumeInfo(isoPath);

        Assert.True(vol.IsValid);
        Assert.Equal(0, vol.DiscLseek);
        Assert.Equal("RAW", vol.DiscFormat);
    }

    [Fact]
    public void Cli_Info_PrintsDiscFormat()
    {
        var srcDir = CreateTempDir("xiso_discfmt_cli_src");
        File.WriteAllText(Path.Combine(srcDir, "readme.txt"), "hello");

        var outputDir = CreateTempDir("xiso_discfmt_cli_out");
        var result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out var isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);

        Assert.Equal(0, Program.Main(["-i", isoPath]));

        Assert.Contains("Disc Format:    RAW", _logCapture.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}