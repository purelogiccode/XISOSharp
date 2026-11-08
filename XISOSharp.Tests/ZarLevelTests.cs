using System.Globalization;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <c>--zar-level</c> option (BRF-07): levels 1/6/22 round-trip
/// through ZArchiveSharp and level 6 stays byte-identical to the default.
/// </summary>
[Collection("Sequential")]
public sealed class ZarLevelTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public ZarLevelTests()
    {
        Logger.Quiet = true;
    }

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_zarlevel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso()
    {
        string source = TempDir();
        File.WriteAllText(Path.Combine(source, "readme.txt"), "zar level fixture\n");
        File.WriteAllBytes(Path.Combine(source, "data.bin"),
            Enumerable.Range(0, 200_000).Select(static i => (byte)(i % 251)).ToArray());
        string iso = Path.Combine(TempDir(), "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));
        return iso;
    }

    private static int Run(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        TextWriter savedOut = Logger.Out;
        TextWriter savedError = Logger.Error;
        try
        {
            Logger.Out = output;
            Logger.Error = error;
            return Program.Main(args);
        }
        finally
        {
            Logger.Out = savedOut;
            Logger.Error = savedError;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(22)]
    public void Zar_Level_RoundTrips(int level)
    {
        string iso = CreateIso();
        string zar = Path.Combine(TempDir(), $"game-{level}.zar");

        Assert.Equal(0, Run("--zar", "-o", zar, "-y", "--zar-level", level.ToString(CultureInfo.InvariantCulture), iso));

        Assert.True(File.Exists(zar));
        IReadOnlyList<ZarEntry> entries = XisoZarchive.ListZar(zar);
        Assert.Contains(entries, static entry => entry.Path.EndsWith("/readme.txt", StringComparison.Ordinal));
        Assert.Contains(entries, static entry => entry.Path.EndsWith("/data.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void Zar_Level6_IsByteIdenticalToDefault()
    {
        string iso = CreateIso();
        string work = TempDir();
        string defaultZar = Path.Combine(work, "default.zar");
        string level6Zar = Path.Combine(work, "level6.zar");

        Assert.Equal(0, Run("--zar", "-o", defaultZar, "-y", iso));
        Assert.Equal(0, Run("--zar", "-o", level6Zar, "-y", "--zar-level", "6", iso));

        Assert.Equal(File.ReadAllBytes(defaultZar), File.ReadAllBytes(level6Zar));
    }

    [Fact]
    public void Convert_ZarLevel_IsAccepted()
    {
        string iso = CreateIso();
        string zar = Path.Combine(TempDir(), "converted.zar");

        Assert.Equal(0, Run("convert", iso, zar, "-y", "--zar-level", "22"));

        Assert.True(File.Exists(zar));
        Assert.NotEmpty(XisoZarchive.ListZar(zar));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("23")]
    [InlineData("abc")]
    public void Zar_InvalidLevel_FailsBeforeWriting(string level)
    {
        string iso = CreateIso();
        string zar = Path.Combine(TempDir(), "invalid.zar");

        Assert.Equal(1, Run("--zar", "-o", zar, "-y", "--zar-level", level, iso));
        Assert.False(File.Exists(zar));
    }

    [Fact]
    public void ZarLevel_WithoutZarMode_Fails()
    {
        string iso = CreateIso();

        Assert.Equal(1, Run("--zar-level", "9", "-l", iso));
    }
}
