using XISOSharp.Cli;
using XISOSharp.Models;
using ZArchiveSharp;

namespace XISOSharp.Tests;

/// <summary>
/// ZArchive (<c>.zar</c>) input: listing, extraction, conversion and the
/// ISO-equivalent tree checksum.
/// </summary>
[Collection("Sequential")]
public sealed class ZarInputTests : IDisposable
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
    public void ListZar_ReturnsTree()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        IReadOnlyList<ZarEntry> entries = XisoZarchive.ListZar(zar);

        Assert.Contains(entries, e => IsEntry(e, "/a.txt") && !e.IsDirectory && e.Size == 5);
        Assert.Contains(entries, e => IsEntry(e, "/b.txt") && !e.IsDirectory && e.Size == 3000);
        Assert.Contains(entries, e => IsEntry(e, "/sub") && e.IsDirectory);
        Assert.Contains(entries, e => IsEntry(e, "/sub/c.txt") && !e.IsDirectory && e.Size == 6);
    }

    private static bool IsEntry(ZarEntry entry, string path) =>
        string.Equals(entry.Path, path, StringComparison.Ordinal);

    [Fact]
    public void ExtractZar_MatchesSourceTree()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string source = Path.Combine(work, "src");
        string extracted = Path.Combine(work, "extracted");

        Assert.Equal(0, XisoZarchive.ExtractZar(zar, extracted));
        AssertSameTree(source, extracted);

        // skipExisting must not throw when files are already there.
        Assert.Equal(0, XisoZarchive.ExtractZar(zar, extracted, skipExisting: true));
        AssertSameTree(source, extracted);
    }

    [Fact]
    public void ExtractZar_EntryLimit_RejectsHostileArchive()
    {
        string work = CreateTempDir("xiso_zar_limits");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string extracted = Path.Combine(work, "limited");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => XisoZarchive.ExtractZar(zar, extracted, maxEntries: 2));
        Assert.Contains("entry limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractZar_ByteLimit_RejectsHostileArchive()
    {
        string work = CreateTempDir("xiso_zar_bytes");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string extracted = Path.Combine(work, "limited");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => XisoZarchive.ExtractZar(zar, extracted, maxTotalBytes: 10));
        Assert.Contains("byte limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractZar_DeeplyNestedArchive_FailsAtMaxTocDepth()
    {
        string work = CreateTempDir("xiso_zar_deep");
        string zar = Path.Combine(work, "deep.zar");
        string deepPath = string.Join('/',
            Enumerable.Range(1, Constants.MaxTocDepth + 1).Select(static i => $"d{i}"));

        using (FileStream output = File.Create(zar))
        using (ZArchiveWriter writer = new(output))
        {
            Assert.True(writer.StartNewFile("a.txt"));
            writer.AppendData("x"u8);
            Assert.True(writer.MakeDir(deepPath, recursive: true));
            writer.Finalize();
        }

        string extracted = Path.Combine(work, "extracted");
        InvalidDataException extractError = Assert.Throws<InvalidDataException>(
            () => XisoZarchive.ExtractZar(zar, extracted));
        Assert.Contains("depth", extractError.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException listError = Assert.Throws<InvalidDataException>(
            () => XisoZarchive.ListZar(zar));
        Assert.Contains("depth", listError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZarChecksum_MatchesIsoChecksum()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        Assert.Equal(
            XisoChecksum.ComputeImageChecksum(iso),
            XisoChecksum.ComputeZarChecksum(zar));
    }

    [Fact]
    public void ImageSource_ClassifiesZarButRefusesSectorOpen()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));

        Assert.True(XisoImageSource.TryDetect(zar, out XisoImageSourceInfo? info));
        Assert.NotNull(info);
        Assert.Equal(XisoImageFormat.Zar, info.Format);
        Assert.Throws<NotSupportedException>(() => XisoImageSource.OpenRead(zar));
    }

    [Fact]
    public void ConvertZarToIso_RoundTripsContent()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string converted = Path.Combine(work, "converted.iso");
        string fromOriginal = Path.Combine(work, "from_original");
        string fromConverted = Path.Combine(work, "from_converted");

        Assert.Equal(0, XisoZarchive.ConvertZarToIso(zar, converted));
        Assert.Equal(0, XisoReader.Extract(iso, fromOriginal, llCompat: false));
        Assert.Equal(0, XisoReader.Extract(converted, fromConverted, llCompat: false));
        AssertSameTree(fromOriginal, fromConverted);

        Assert.Throws<IOException>(() => XisoZarchive.ConvertZarToIso(zar, converted));
    }

    [Fact]
    public void Cli_ListExtractAndChecksum_Zar()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string output = Path.Combine(work, "cli_extract");

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["-l", zar]));
            Assert.Contains("a.txt", capture.Output, StringComparison.Ordinal);
        }

        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["--unpack", zar, output]));
            Assert.True(Directory.Exists(output), capture.Output);
        }

        Assert.Equal("hello", File.ReadAllText(Path.Combine(output, "a.txt")));

        string isoHash = XisoChecksum.ComputeImageChecksumHex(iso);
        using (LogCapture capture = new())
        {
            Assert.Equal(0, Program.Main(["checksum", zar]));
            Assert.Contains(isoHash, capture.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cli_Convert_ZarToIso()
    {
        string work = CreateTempDir("xiso_zar");
        string iso = CreateIso(work);
        string zar = CreateZar(iso, Path.Combine(work, "game.zar"));
        string output = Path.Combine(work, "converted.iso");

        using LogCapture capture = new();
        Assert.Equal(0, Program.Main(["convert", zar, output, "-y"]));
        Assert.True(File.Exists(output), capture.Output);

        Assert.Equal(
            XisoChecksum.ComputeImageChecksum(iso),
            XisoChecksum.ComputeImageChecksum(output));
    }

    private static string CreateZar(string iso, string zar)
    {
        Assert.True(XisoZarchive.CreateZar(iso, zar, quiet: true));
        return zar;
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

    private static void AssertSameTree(string dirA, string dirB)
    {
        string[] filesA = Directory.GetFiles(dirA, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirA, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string[] filesB = Directory.GetFiles(dirB, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dirB, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(filesA, filesB);
        foreach (string rel in filesA)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dirA, rel)), File.ReadAllBytes(Path.Combine(dirB, rel)));
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
