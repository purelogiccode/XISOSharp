using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the scenario-tuned extraction copier (TODO #8, xdvdfs #167):
/// exact-byte copies across chunk boundaries, truncation/cancellation/error
/// behavior, pooled-buffer path, and the per-chunk <c>FileProgress</c> channel
/// on unpack and copy-out.
/// </summary>
[Collection("Sequential")]
public class XisoFileCopierTests : IDisposable
{
    private const int TwoMb = 2 * 1024 * 1024;

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

    private sealed class CollectingProgress : IProgress<ProgressInfo>
    {
        public List<ProgressInfo> Events { get; } = [];

        public void Report(ProgressInfo value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }

    private sealed class ShortReadStream : MemoryStream
    {
        private readonly int _maxPerRead;

        public ShortReadStream(byte[] data, int maxPerRead)
            : base(data, writable: false)
        {
            _maxPerRead = maxPerRead;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, _maxPerRead));
    }

    private static byte[] RandomBytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(65536)]
    [InlineData(TwoMb - 1)]
    [InlineData(TwoMb)]
    [InlineData(TwoMb + 1)]
    [InlineData(3 * 1024 * 1024)]
    public void CopyExact_RoundTrips_AllSizes(int size)
    {
        var data = RandomBytes(size, 1234);
        using var source = new MemoryStream(data, writable: false);
        using var dest = new MemoryStream();
        var progress = new List<long>();

        var copied = XisoFileCopier.CopyExact(
            source, size,
            // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
            (buffer, count) => dest.Write(buffer, 0, count),
            new byte[TwoMb],
            progress.Add);

        Assert.Equal(size, copied);
        Assert.Equal(data, dest.ToArray());
        if (size == 0)
        {
            Assert.Empty(progress);
        }
        else
        {
            Assert.NotEmpty(progress);
            Assert.Equal(size, progress[^1]);
            Assert.Equal(progress.OrderBy(static x => x).ToArray(), progress.ToArray());
        }
    }

    [Fact]
    public void CopyExact_ZeroBytes_InvokesNeitherCallback()
    {
        using var source = new MemoryStream([1, 2, 3], writable: false);
        var chunks = 0;
        var progress = 0;

        var copied = XisoFileCopier.CopyExact(
            source, 0,
            (_, _) => chunks++,
            new byte[TwoMb],
            _ => progress++);

        Assert.Equal(0, copied);
        Assert.Equal(0, chunks);
        Assert.Equal(0, progress);
    }

    [Fact]
    public void CopyExact_ShortSource_ThrowsTruncatedWithCounts()
    {
        using var source = new MemoryStream(new byte[300], writable: false);

        var ex = Assert.Throws<TruncatedCopyException>(() =>
            XisoFileCopier.CopyExact(source, 1000, (_, _) => { }, new byte[TwoMb]));

        Assert.Equal(1000, ex.ExpectedBytes);
        Assert.Equal(300, ex.CopiedBytes);
    }

    [Fact]
    public void CopyExact_ShortReads_StitchedExactly()
    {
        var data = RandomBytes(10000, 99);
        using var source = new ShortReadStream(data, maxPerRead: 777);
        using var dest = new MemoryStream();

        var copied = XisoFileCopier.CopyExact(
            source, data.Length,
            // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
            (buffer, count) => dest.Write(buffer, 0, count),
            new byte[TwoMb]);

        Assert.Equal(data.Length, copied);
        Assert.Equal(data, dest.ToArray());
    }

    [Fact]
    public void CopyExact_NullBuffer_RentsPooledBuffer()
    {
        var data = RandomBytes(3 * 1024 * 1024, 7);
        using var source = new MemoryStream(data, writable: false);
        using var dest = new MemoryStream();

        var copied = XisoFileCopier.CopyExact(
            source, data.Length,
            // ReSharper disable once AccessToDisposedClosure — sink runs synchronously inside CopyExact.
            (buffer, count) => dest.Write(buffer, 0, count),
            buffer: null);

        Assert.Equal(data.Length, copied);
        Assert.Equal(data, dest.ToArray());
    }

    [Fact]
    public void CopyExact_EmptyBuffer_Throws()
    {
        using var source = new MemoryStream([1], writable: false);
        Assert.Throws<ArgumentException>(() =>
            XisoFileCopier.CopyExact(source, 1, (_, _) => { }, Array.Empty<byte>()));
    }

    [Fact]
    public void CopyExact_NegativeCount_Throws()
    {
        using var source = new MemoryStream([1], writable: false);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoFileCopier.CopyExact(source, -1, (_, _) => { }, new byte[TwoMb]));
    }

    [Fact]
    public void CopyExact_PreCancelledToken_Throws()
    {
        using var source = new MemoryStream(new byte[100], writable: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            XisoFileCopier.CopyExact(source, 100, (_, _) => { }, new byte[TwoMb],
                cancellationToken: cts.Token));
    }

    [Fact]
    public void CopyExact_CancelMidCopy_AbortsPromptly()
    {
        var data = RandomBytes(3 * 1024 * 1024, 11);
        using var source = new MemoryStream(data, writable: false);
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            XisoFileCopier.CopyExact(
                source, data.Length,
                (_, _) => { },
                new byte[TwoMb],
                // ReSharper disable once AccessToDisposedClosure — callback runs synchronously inside CopyExact.
                _ => cts.Cancel(),
                cts.Token));
    }

    [Fact]
    public void CopyExact_SinkException_PropagatesUnwrapped()
    {
        using var source = new MemoryStream(new byte[100], writable: false);
        var boom = new InvalidOperationException("boom");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            XisoFileCopier.CopyExact(source, 100, (_, _) => throw boom, new byte[TwoMb]));

        Assert.Same(boom, ex);
    }

    private string CreateIsoWithSizedFiles(out byte[] big, out byte[] small)
    {
        var src = CreateTempDir("xiso_copier_src");
        big = RandomBytes(3 * 1024 * 1024, 4242);
        small = RandomBytes(5000, 43);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
        File.WriteAllBytes(Path.Combine(src, "small.txt"), small);
        File.WriteAllBytes(Path.Combine(src, "empty.txt"), Array.Empty<byte>());

        var outDir = CreateTempDir("xiso_copier_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null));
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void CopyOut_ReportsFileProgress_PerChunk()
    {
        var isoPath = CreateIsoWithSizedFiles(out var big, out _);
        var progress = new CollectingProgress();
        var dest = Path.Combine(CreateTempDir("xiso_copier_dest"), "big.bin");

        XisoReader.CopyOut(isoPath, "/big.bin", dest, progress: progress);

        Assert.Equal(big, File.ReadAllBytes(dest));
        var events = progress.Events.Where(static e => e.Type == ProgressInfoType.FileProgress).ToList();
        // 3 MB through a 2 MB buffer = exactly two chunks.
        Assert.Equal(2, events.Count);
        Assert.Equal(TwoMb, events[0].Size);
        Assert.Equal(big.Length, events[1].Size);
        Assert.All(events, e => Assert.Equal(big.Length, e.Count));
        Assert.All(events, static e => Assert.Equal("/big.bin", e.Path));

        var entry = XisoReader.GetEntryInfo(isoPath, "/big.bin");
        Assert.NotNull(entry);
        Assert.All(events, e => Assert.Equal(entry.StartSector, (uint)e.Sector));
    }

    [Fact]
    public void CopyOut_SmallAndEmptyFiles_ProgressShape()
    {
        var isoPath = CreateIsoWithSizedFiles(out _, out var small);
        var progress = new CollectingProgress();

        var smallDest = Path.Combine(CreateTempDir("xiso_copier_dest"), "small.txt");
        XisoReader.CopyOut(isoPath, "/small.txt", smallDest, progress: progress);
        Assert.Equal(small, File.ReadAllBytes(smallDest));

        var smallEvents = progress.Events.Where(static e => e.Type == ProgressInfoType.FileProgress).ToList();
        var single = Assert.Single(smallEvents);
        Assert.Equal(small.Length, single.Size);
        Assert.Equal(small.Length, single.Count);

        progress.Events.Clear();
        var emptyDest = Path.Combine(CreateTempDir("xiso_copier_dest"), "empty.txt");
        XisoReader.CopyOut(isoPath, "/empty.txt", emptyDest, progress: progress);
        Assert.Equal(0, new FileInfo(emptyDest).Length);
        Assert.DoesNotContain(progress.Events, static e => e.Type == ProgressInfoType.FileProgress);
    }

    [Fact]
    public void UnpackImage_ReportsFileProgress_AndStillReportsFileAdded()
    {
        var isoPath = CreateIsoWithSizedFiles(out var big, out var small);
        var progress = new CollectingProgress();
        var dest = CreateTempDir("xiso_copier_unpack");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest, progress: progress));

        Assert.Equal(big, File.ReadAllBytes(Path.Combine(dest, "big.bin")));
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(dest, "small.txt")));

        var added = progress.Events
            .Where(static e => e.Type == ProgressInfoType.FileAdded)
            .ToList();
        Assert.Equal(3, added.Count);

        var byFile = progress.Events
            .Where(static e => e.Type == ProgressInfoType.FileProgress)
            .GroupBy(static e => e.Path, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key!, static g => g.OrderBy(static e => e.Size).ToList(),
                StringComparer.Ordinal);

        // Every non-empty file ends its progress at its full size; the empty
        // file reports FileAdded but no FileProgress (nothing to copy).
        foreach (var file in added)
        {
            if (file.Size == 0)
            {
                Assert.DoesNotContain(file.Path!, byFile.Keys, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Equal(file.Size, byFile[file.Path!][^1].Size);
            }
        }
    }

    [Fact]
    public void CopyOut_LargeFile_ByteIdentical()
    {
        var src = CreateTempDir("xiso_copier_src");
        var data = RandomBytes(5 * 1024 * 1024, 2026);
        File.WriteAllBytes(Path.Combine(src, "large.bin"), data);

        var outDir = CreateTempDir("xiso_copier_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null));
        Assert.NotNull(isoPath);

        var dest = Path.Combine(CreateTempDir("xiso_copier_dest"), "large.bin");
        XisoReader.CopyOut(isoPath, "/large.bin", dest);
        Assert.Equal(data, File.ReadAllBytes(dest));

        var hash = XisoReader.ComputeFileHash(isoPath, "/large.bin", HashAlgorithmName.SHA256);
        Assert.Equal(SHA256.HashData(data), hash);
    }
}
