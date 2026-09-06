using System.Security.Cryptography;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XisoSplitter"/> plain-ISO splitting (TODO #17, xdvdfs #97):
/// split/join round-trips, halves, sector alignment, part naming, guards,
/// progress/cancel semantics, and the <c>split</c>/<c>join</c> CLI verbs.
/// </summary>
[Collection("Sequential")]
public class XisoSplitTests : IDisposable
{
    private const int Sector = 2048;
    private const long PartSize = 32L * Sector; // 65536

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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
                // ignored
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

    private static void PopulateSplitSource(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "split me");
        var big = new byte[200_000];
        new Random(1234).NextBytes(big);
        File.WriteAllBytes(Path.Combine(dir, "big.bin"), big);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), "nested");
    }

    private string CreateIso(string srcDir)
    {
        var outDir = CreateTempDir("xiso_split_out");
        Assert.Equal(0, XisoWriter.CreateXiso(srcDir, outDir, null, null, out var isoPath, null, null));
        return isoPath;
    }

    private string CreateSplitIso()
    {
        var src = CreateTempDir("xiso_split_src");
        PopulateSplitSource(src);
        return CreateIso(src);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public void Split_Join_RoundTrip_ByteIdentical()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var length = new FileInfo(iso).Length;
        Assert.True(length > PartSize * 2);

        // Exercise the XisoReader facade here; the engine is covered directly below.
        var parts = XisoReader.SplitXiso(iso, Path.Combine(work, "game"), PartSize);
        Assert.True(parts.Count >= 3);
        Assert.Equal(Path.Combine(work, "game.1.iso"), parts[0]);

        var joined = Path.Combine(work, "rejoined.iso");
        Assert.Equal(joined, XisoReader.JoinSplitXiso(parts[0], joined));
        Assert.Equal(Sha(File.ReadAllBytes(iso)), Sha(File.ReadAllBytes(joined)));
    }

    [Fact]
    public void SplitHalves_TwoParts_JoinIdentical()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var length = new FileInfo(iso).Length;

        var parts = XisoSplitter.SplitHalves(iso, Path.Combine(work, "half"));
        Assert.Equal(2, parts.Count);
        Assert.Equal(Path.Combine(work, "half.1.iso"), parts[0]);
        Assert.Equal(Path.Combine(work, "half.2.iso"), parts[1]);

        var first = new FileInfo(parts[0]).Length;
        var second = new FileInfo(parts[1]).Length;
        Assert.Equal(0, first % Sector);
        Assert.Equal(length, first + second);
        Assert.True(first >= (length + 1) / 2);

        var joined = Path.Combine(work, "rejoined.iso");
        XisoSplitter.Join(parts[0], joined);
        Assert.Equal(Sha(File.ReadAllBytes(iso)), Sha(File.ReadAllBytes(joined)));
    }

    [Fact]
    public void Split_Parts_SectorAligned_ExceptLast()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var length = new FileInfo(iso).Length;

        var parts = XisoSplitter.Split(iso, Path.Combine(work, "game"), PartSize);
        Assert.True(parts.Count >= 2);
        for (var i = 0; i < parts.Count - 1; i++)
            Assert.Equal(PartSize, new FileInfo(parts[i]).Length);
        var last = new FileInfo(parts[^1]).Length;
        Assert.True(last > 0 && last <= PartSize);
        Assert.Equal(length, PartSize * (parts.Count - 1) + last);
    }

    [Fact]
    public void Split_FitsInOnePart_SinglePart()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");

        var parts = XisoSplitter.Split(iso, Path.Combine(work, "game"), long.MaxValue);
        Assert.Single(parts);
        Assert.Equal(Path.Combine(work, "game.1.iso"), parts[0]);

        var joined = Path.Combine(work, "rejoined.iso");
        XisoSplitter.Join(parts[0], joined);
        Assert.Equal(Sha(File.ReadAllBytes(iso)), Sha(File.ReadAllBytes(joined)));
    }

    [Fact]
    public void PartPath_Naming_And_IsSplitPath()
    {
        Assert.Equal("game.1.iso", XisoSplitter.PartPath("game", 0));
        Assert.Equal("game.2.iso", XisoSplitter.PartPath("game", 1));
        var withExt = Path.Combine("d", "game.iso");
        Assert.Equal(Path.Combine("d", "game.1.iso"), XisoSplitter.PartPath(withExt, 0));
        Assert.True(XisoSplitter.IsSplitPath(Path.Combine("d", "game.1.iso")));
        Assert.True(XisoSplitter.IsSplitPath("GAME.1.ISO"));
        Assert.False(XisoSplitter.IsSplitPath(Path.Combine("d", "game.2.iso")));
        Assert.False(XisoSplitter.IsSplitPath(Path.Combine("d", "game.iso")));
        Assert.False(XisoSplitter.IsSplitPath(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoSplitter.PartPath("game", -1));
        Assert.Throws<ArgumentException>(() => XisoSplitter.PartPath("", 0));
    }

    [Fact]
    public void Split_Guards()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var okBase = Path.Combine(work, "game");

        Assert.Throws<ArgumentException>(() => XisoSplitter.Split("", okBase, PartSize));
        Assert.Throws<ArgumentException>(() => XisoSplitter.Split(iso, "", PartSize));
        Assert.Throws<FileNotFoundException>(() =>
            XisoSplitter.Split(Path.Combine(work, "missing.iso"), okBase, PartSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => XisoSplitter.Split(iso, okBase, Sector - 1));

        var garbage = Path.Combine(work, "garbage.iso");
        var garbageBytes = new byte[100_000];
        new Random(7).NextBytes(garbageBytes);
        File.WriteAllBytes(garbage, garbageBytes);
        Assert.Throws<XisoFormatException>(() => XisoSplitter.Split(garbage, okBase, PartSize));
        Assert.Throws<XisoFormatException>(() => XisoSplitter.SplitHalves(garbage, okBase));
    }

    [Fact]
    public void Split_RefusesExistingParts_BeforeWriting()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var basePath = Path.Combine(work, "game");

        var parts = XisoSplitter.Split(iso, basePath, PartSize);
        var firstBytes = File.ReadAllBytes(parts[0]);
        Assert.Throws<IOException>(() => XisoSplitter.Split(iso, basePath, PartSize));
        // Pre-existing parts are left untouched.
        Assert.Equal(Sha(firstBytes), Sha(File.ReadAllBytes(parts[0])));
    }

    [Fact]
    public void Join_Guards()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var parts = XisoSplitter.Split(iso, Path.Combine(work, "game"), PartSize);
        Assert.True(parts.Count >= 2);

        Assert.Throws<ArgumentException>(() => XisoSplitter.Join("", Path.Combine(work, "o.iso")));
        Assert.Throws<ArgumentException>(() =>
            XisoSplitter.Join(Path.Combine(work, "o.iso"), Path.Combine(work, "p.iso")));
        // Must start at part 1, not part 2.
        Assert.Throws<ArgumentException>(() => XisoSplitter.Join(parts[1], Path.Combine(work, "o.iso")));
        Assert.Throws<FileNotFoundException>(() =>
            XisoSplitter.Join(Path.Combine(work, "missing.1.iso"), Path.Combine(work, "o.iso")));

        var existing = Path.Combine(work, "existing.iso");
        File.WriteAllBytes(existing, [1, 2, 3]);
        Assert.Throws<IOException>(() => XisoSplitter.Join(parts[0], existing));
        // Refused outputs are left untouched.
        Assert.Equal([1, 2, 3], File.ReadAllBytes(existing));

        // The output must not be one of the parts.
        Assert.Throws<ArgumentException>(() => XisoSplitter.Join(parts[0], parts[0]));
    }

    [Fact]
    public void Join_GarbageParts_Rejected_And_OutputRemoved()
    {
        var work = CreateTempDir("xiso_split_work");
        var first = Path.Combine(work, "garbage.1.iso");
        File.WriteAllBytes(first, new byte[100_000]);
        var output = Path.Combine(work, "joined.iso");

        Assert.Throws<XisoFormatException>(() => XisoSplitter.Join(first, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Join_ByteExactConcat_OrderProof()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var parts = XisoSplitter.Split(iso, Path.Combine(work, "game"), PartSize);
        Assert.True(parts.Count >= 2);

        // Corrupt the middle of part 2: join must reproduce the corruption at
        // exactly that global offset (order/offsets proof), not fail or reorder.
        var part2Bytes = File.ReadAllBytes(parts[1]);
        part2Bytes[100] ^= 0xFF;
        File.WriteAllBytes(parts[1], part2Bytes);

        var joined = Path.Combine(work, "joined.iso");
        var expected = parts.SelectMany(File.ReadAllBytes).ToArray();
        XisoReader.JoinSplitXiso(parts[0], joined);
        Assert.Equal(Sha(expected), Sha(File.ReadAllBytes(joined)));
        Assert.NotEqual(Sha(File.ReadAllBytes(iso)), Sha(expected), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Split_Progress_Monotonic_FullCoverage()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var length = new FileInfo(iso).Length;
        var events = new List<ProgressInfo>();

        var progress = new ProgressRecorder(events);
        XisoSplitter.Split(iso, Path.Combine(work, "game"), PartSize, progress: progress);

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(ProgressInfoType.FileProgress, e.Type));
        Assert.All(events, e => Assert.Equal(length, e.Count));
        long prev = 0;
        foreach (var e in events)
        {
            Assert.True(e.Size > prev);
            prev = e.Size;
        }

        Assert.Equal(length, events[^1].Size);
    }

    [Fact]
    public void Split_Cancelled_CleansUpParts()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var basePath = Path.Combine(work, "game");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            XisoSplitter.Split(iso, basePath, PartSize, cts.Token));
        Assert.False(File.Exists(XisoSplitter.PartPath(basePath, 0)));
    }

    [Fact]
    public void Join_Cancelled_CreatesNoOutput()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var parts = XisoSplitter.Split(iso, Path.Combine(work, "game"), PartSize);
        var output = Path.Combine(work, "joined.iso");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => XisoSplitter.Join(parts[0], output, cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Cli_Split_Join_EndToEnd()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var basePath = Path.Combine(work, "game");

        Assert.Equal(0, Program.Main(["split", "--size", "65536", "--output", basePath, iso]));
        Assert.True(File.Exists(basePath + ".1.iso"));
        Assert.True(File.Exists(basePath + ".2.iso"));

        var joined = Path.Combine(work, "rejoined.iso");
        Assert.Equal(0, Program.Main(["join", "--output", joined, basePath + ".1.iso"]));
        Assert.Equal(Sha(File.ReadAllBytes(iso)), Sha(File.ReadAllBytes(joined)));

        // Re-splitting onto existing parts fails; bad usages fail.
        Assert.Equal(1, Program.Main(["split", "--size", "65536", "--output", basePath, iso]));
        Assert.Equal(1, Program.Main(["split"]));
        Assert.Equal(1, Program.Main(["split", "--size", "bogus", iso]));
        Assert.Equal(1, Program.Main(["split", "--size", "100", iso]));
        Assert.Equal(1, Program.Main(["join"]));
        Assert.Equal(1, Program.Main(["join", Path.Combine(work, "nope.1.iso")]));
    }

    [Fact]
    public void Cli_Split_Halves_And_Defaults()
    {
        var iso = CreateSplitIso();
        var work = CreateTempDir("xiso_split_work");
        var staged = Path.Combine(work, "staged.iso");
        File.Copy(iso, staged);

        // No --size (4G default → single part) and no --output (input-derived base).
        Assert.Equal(0, Program.Main(["split", staged]));
        var first = Path.Combine(work, "staged.1.iso");
        Assert.True(File.Exists(first));

        Assert.Equal(0, Program.Main(["split", "--size", "half", "--output", Path.Combine(work, "h"), iso]));
        Assert.True(File.Exists(Path.Combine(work, "h.1.iso")));
        Assert.True(File.Exists(Path.Combine(work, "h.2.iso")));
    }

    private sealed class ProgressRecorder(List<ProgressInfo> events) : IProgress<ProgressInfo>
    {
        private readonly List<ProgressInfo> _events = events;
        public void Report(ProgressInfo value) => _events.Add(value);
    }
}
