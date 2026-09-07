using System.Security.Cryptography;
using XISOSharp.Interfaces;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the <see cref="IFilesystem"/> destination abstraction (TODO #7,
/// xdvdfs #166): <see cref="LocalFilesystem"/> and <see cref="MemoryFilesystem"/>
/// semantics, the filesystem-aware <see cref="UnpackOptions.ShouldSkip(string, long, IFilesystem)"/>,
/// and byte-parity of the filesystem-based <c>UnpackImage</c> overloads against
/// the legacy disk unpack.
/// </summary>
[Collection("Sequential")]
public class XisoFilesystemTests : IDisposable
{
    private static readonly string FixtureIsoPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "test_fixture.iso"));

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
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

    /// <summary>Synchronous progress sink: handlers run inline on the unpacking thread.</summary>
    private sealed class InlineProgress(Action<ProgressInfo> callback) : IProgress<ProgressInfo>
    {
        private readonly Action<ProgressInfo> _callback = callback;
        public void Report(ProgressInfo info) => _callback(info);
    }

    /// <summary>Destination that fails one exact path, for continue-on-error tests.</summary>
    private sealed class ThrowingFilesystem(IFilesystem inner, string bombPath) : IFilesystem
    {
        private readonly string _bombPath = bombPath;
        private readonly IFilesystem _inner = inner;

        public Stream CreateFile(string path) =>
            path.Equals(_bombPath, StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("disk full")
                : _inner.CreateFile(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool FileExists(string path) => _inner.FileExists(path);

        public long FileLength(string path) => _inner.FileLength(path);
    }

    // ------------------------------------------------------------------
    // LocalFilesystem
    // ------------------------------------------------------------------

    [Fact]
    public void LocalFilesystem_CreateFile_WritesUnderRoot()
    {
        var root = CreateTempDir("xiso_fs_root");
        var fs = new LocalFilesystem(root);
        fs.CreateDirectory("sub");

        using (var s = fs.CreateFile("sub/a.bin"))
        {
            s.Write([1, 2, 3, 4]);
        }

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(root, "sub", "a.bin")));
    }

    [Fact]
    public void LocalFilesystem_CreateFile_MissingDirectory_Throws()
    {
        var root = CreateTempDir("xiso_fs_root");
        var fs = new LocalFilesystem(root);

        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateFile("no/such/dir/f.bin"));
    }

    [Fact]
    public void LocalFilesystem_ExistsAndLength_MissingReturnsMinusOne()
    {
        var root = CreateTempDir("xiso_fs_root");
        var fs = new LocalFilesystem(root);
        var path = Path.Combine(root, "f.bin");
        File.WriteAllBytes(path, "\t\t\t"u8.ToArray());

        Assert.True(fs.FileExists("f.bin"));
        Assert.Equal(3, fs.FileLength("f.bin"));
        Assert.False(fs.FileExists("missing.bin"));
        Assert.Equal(-1, fs.FileLength("missing.bin"));
        Assert.False(fs.FileExists("/no/dir/missing.bin"));
    }

    [Fact]
    public void LocalFilesystem_LeadingSlashAndSeparators_NormalizedToSameFile()
    {
        var root = CreateTempDir("xiso_fs_root");
        var fs = new LocalFilesystem(root);
        fs.CreateDirectory("/d1");

        using (var s = fs.CreateFile("/d1/f.txt"))
        {
            s.Write([1, 1]);
        }

        // Re-creating via the other spelling truncates the same file.
        using (var s = fs.CreateFile("d1\\f.txt"))
        {
            s.Write([2]);
        }

        Assert.Equal([2], File.ReadAllBytes(Path.Combine(root, "d1", "f.txt")));
        Assert.Equal(1, fs.FileLength("d1/f.txt"));
    }

    [Fact]
    public void LocalFilesystem_NullRoot_ResolvesAgainstCurrentDirectory()
    {
        var cwd = CreateTempDir("xiso_fs_cwd");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(cwd);
            var fs = LocalFilesystem.Instance;

            using (var s = fs.CreateFile("rel.bin"))
            {
                s.Write([7]);
            }

            Assert.Equal([7], File.ReadAllBytes(Path.Combine(cwd, "rel.bin")));
            Assert.Equal(1, fs.FileLength("rel.bin"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void LocalFilesystem_EscapeAttempts_StayInsideRoot()
    {
        // BUG-LIB-019: ".." climbs and rooted spellings must never resolve
        // outside the destination root.
        var root = CreateTempDir("xiso_fs_root");
        var fs = new LocalFilesystem(root);

        Assert.False(fs.FileExists("../evil.bin"));
        Assert.Equal(-1, fs.FileLength("../evil.bin"));

        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateDirectory("../evil"));
        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateFile("../evil.bin"));

        var parent = Path.GetDirectoryName(root)!;
        Assert.False(File.Exists(Path.Combine(parent, "evil.bin")));
        Assert.False(Directory.Exists(Path.Combine(parent, "evil")));
    }

    // ------------------------------------------------------------------
    // MemoryFilesystem
    // ------------------------------------------------------------------

    [Fact]
    public void MemoryFilesystem_CommitsOnDispose_RecreateReplaces()
    {
        var fs = new MemoryFilesystem();

        using (var s = fs.CreateFile("a.bin"))
        {
            s.Write([1, 2, 3, 4, 5]);
        }

        Assert.Equal([1, 2, 3, 4, 5], fs.ReadAllBytes("a.bin"));
        Assert.Equal(5, fs.FileLength("a.bin"));

        using (var s = fs.CreateFile("a.bin"))
        {
            s.Write([9]);
        }

        Assert.Equal([9], fs.ReadAllBytes("a.bin"));
        Assert.Equal(1, fs.FileLength("a.bin"));
    }

    [Fact]
    public void MemoryFilesystem_AutoCreatesParents_AndTracksDirectories()
    {
        var fs = new MemoryFilesystem();

        using (var s = fs.CreateFile("/x/y/z.bin"))
        {
            s.Write([1]);
        }

        Assert.Equal(["x/y/z.bin"], fs.FileNames, StringComparer.Ordinal);
        Assert.Contains("x", fs.DirectoryNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("x/y", fs.DirectoryNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("x/y/z.bin", fs.DirectoryNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryFilesystem_EmptyFile_LengthZero()
    {
        var fs = new MemoryFilesystem();
        using (fs.CreateFile("empty.bin"))
        {
        }

        Assert.True(fs.FileExists("empty.bin"));
        Assert.Equal(0, fs.FileLength("empty.bin"));
        Assert.Empty(fs.ReadAllBytes("empty.bin"));
    }

    [Fact]
    public void MemoryFilesystem_MissingFile_MinusOne()
    {
        var fs = new MemoryFilesystem();
        Assert.False(fs.FileExists("nope.bin"));
        Assert.Equal(-1, fs.FileLength("nope.bin"));
    }

    [Fact]
    public void MemoryFilesystem_CreateFileAtRoot_Throws()
    {
        var fs = new MemoryFilesystem();
        Assert.Throws<ArgumentException>(() => fs.CreateFile("/"));
    }

    // ------------------------------------------------------------------
    // UnpackOptions.ShouldSkip filesystem overload
    // ------------------------------------------------------------------

    [Fact]
    public void ShouldSkip_FilesystemOverload_MatchesMemorySemantics()
    {
        var fs = new MemoryFilesystem();
        using (var s = fs.CreateFile("a.bin"))
        {
            s.Write(new byte[10]);
        }

        var skip = new UnpackOptions { SkipExisting = true };
        Assert.True(skip.ShouldSkip("a.bin", 10, fs));
        Assert.False(skip.ShouldSkip("a.bin", 11, fs));
        Assert.False(skip.ShouldSkip("missing.bin", 10, fs));
        Assert.False(skip.ShouldSkip("", 10, fs));

        var noSkip = new UnpackOptions();
        Assert.False(noSkip.ShouldSkip("a.bin", 10, fs));
    }

    [Fact]
    public void ShouldSkip_LegacyOverload_StillProbesDisk()
    {
        var dir = CreateTempDir("xiso_fs_skip");
        var path = Path.Combine(dir, "on_disk.bin");
        File.WriteAllBytes(path, new byte[4]);

        var skip = new UnpackOptions { SkipExisting = true };
        Assert.True(skip.ShouldSkip(path, 4));
        Assert.False(skip.ShouldSkip(path, 5));
        Assert.False(skip.ShouldSkip(Path.Combine(dir, "nope.bin"), 4));
    }

    // ------------------------------------------------------------------
    // UnpackImage over a filesystem — parity with the legacy disk unpack
    // ------------------------------------------------------------------

    private static string NormalizeRel(string relative) => relative.Replace('\\', '/');

    private static Dictionary<string, byte[]> HashDiskTree(string root)
    {
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            hashes[NormalizeRel(Path.GetRelativePath(root, file))] = SHA256.HashData(File.ReadAllBytes(file));
        }

        return hashes;
    }

    private static HashSet<string> DiskDirectories(string root)
    {
        return Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => NormalizeRel(Path.GetRelativePath(root, d)))
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_MatchesDiskUnpack()
    {
        Assert.True(File.Exists(FixtureIsoPath), $"Reference fixture missing: {FixtureIsoPath}");

        var memory = new MemoryFilesystem();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, memory));

        var diskDir = CreateTempDir("xiso_fs_disk");
        Assert.Equal(0, XisoReader.Extract(FixtureIsoPath, diskDir, false));

        var expectedFiles = HashDiskTree(diskDir);
        Assert.NotEmpty(expectedFiles);
        Assert.Equal(
            expectedFiles.Keys.OrderBy(static k => k, StringComparer.Ordinal),
            memory.FileNames.OrderBy(static k => k, StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var (name, hash) in expectedFiles)
        {
            Assert.True(SHA256.HashData(memory.ReadAllBytes(name)).SequenceEqual(hash),
                $"content mismatch in memory filesystem: {name}");
        }

        Assert.Equal(DiskDirectories(diskDir), memory.DirectoryNames.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public void UnpackImage_StreamOverload_MatchesPathOverload()
    {
        var byPath = new MemoryFilesystem();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, byPath));

        var byStream = new MemoryFilesystem();
        using (var stream = File.OpenRead(FixtureIsoPath))
        {
            Assert.Equal(0, XisoReader.UnpackImage(stream, "test_fixture.iso", byStream));
        }

        Assert.Equal(
            byPath.FileNames.OrderBy(static k => k, StringComparer.Ordinal),
            byStream.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
        foreach (var name in byPath.FileNames)
        {
            Assert.True(byPath.ReadAllBytes(name).SequenceEqual(byStream.ReadAllBytes(name)),
                $"stream overload content mismatch: {name}");
        }
    }

    [Fact]
    public void UnpackImage_LocalFilesystem_MatchesLegacyUnpack_AndNeverChangesCwd()
    {
        var cwdBefore = Directory.GetCurrentDirectory();

        var root = CreateTempDir("xiso_fs_local_root");
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, new LocalFilesystem(root)));
        Assert.Equal(cwdBefore, Directory.GetCurrentDirectory());

        var legacy = CreateTempDir("xiso_fs_local_legacy");
        Assert.Equal(0, XisoReader.Extract(FixtureIsoPath, legacy, false));

        Assert.Equal(
            HashDiskTree(root).Keys.OrderBy(static k => k, StringComparer.Ordinal),
            HashDiskTree(legacy).Keys.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_ReportsProgress()
    {
        var memory = new MemoryFilesystem();
        var added = new List<ProgressInfo>();
        var chunks = new List<ProgressInfo>();

        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, memory, progress: new InlineProgress(info =>
        {
            switch (info.Type)
            {
                case ProgressInfoType.FileAdded:
                    added.Add(info);
                    break;
                case ProgressInfoType.FileProgress:
                    chunks.Add(info);
                    break;
            }
        })));

        Assert.Equal(memory.FileNames.Count, added.Count);

        // Every nonzero file reports per-chunk progress ending at its full size;
        // zero-byte files emit no chunk events.
        var lastPerPath = new Dictionary<string, ProgressInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            Assert.NotNull(chunk.Path);
            Assert.True(chunk.Size <= chunk.Count, $"progress overshoot for {chunk.Path}");
            if (lastPerPath.TryGetValue(chunk.Path!, out var previous))
            {
                Assert.True(chunk.Size > previous.Size, $"non-monotonic progress for {chunk.Path}");
            }

            lastPerPath[chunk.Path!] = chunk;
        }

        foreach (var name in memory.FileNames.Where(n => memory.FileLength(n) > 0))
        {
            var internalPath = "/" + name;
            Assert.True(lastPerPath.ContainsKey(internalPath), $"no FileProgress events for {internalPath}");
            Assert.Equal(memory.FileLength(name), lastPerPath[internalPath].Count);
            Assert.Equal(memory.FileLength(name), lastPerPath[internalPath].Size);
        }

        Assert.DoesNotContain(lastPerPath.Keys, static p => p.EndsWith("empty.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_SkipExisting_Resumes()
    {
        var memory = new MemoryFilesystem();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, memory));
        var fullNames = memory.FileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Same-size placeholders stay untouched; the whole tree re-lists.
        var resumed = new MemoryFilesystem();
        using (var s = resumed.CreateFile("readme.txt"))
        {
            s.Write(new byte[(int)memory.FileLength("readme.txt")]);
        }

        var options = new UnpackOptions { SkipExisting = true };
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, resumed, options: options));
        Assert.Equal(fullNames.OrderBy(static k => k, StringComparer.Ordinal),
            resumed.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
        Assert.Equal(new byte[(int)memory.FileLength("readme.txt")], resumed.ReadAllBytes("readme.txt"));

        // Without SkipExisting the placeholder is overwritten with real content.
        var rewritten = new MemoryFilesystem();
        using (var s = rewritten.CreateFile("readme.txt"))
        {
            s.Write(new byte[(int)memory.FileLength("readme.txt")]);
        }

        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, rewritten));
        Assert.True(memory.ReadAllBytes("readme.txt").SequenceEqual(rewritten.ReadAllBytes("readme.txt")));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_ContinueOnError_RecordsFailureAndContinues()
    {
        var inner = new MemoryFilesystem();
        var failing = new ThrowingFilesystem(inner, "/readme.txt");

        var ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(FixtureIsoPath, failing,
                options: new UnpackOptions { ContinueOnError = true }));
        Assert.Contains("readme.txt", ex.Message, StringComparison.Ordinal);

        // Every other file still landed in the inner filesystem.
        var healthy = new MemoryFilesystem();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, healthy));
        Assert.Equal(
            healthy.FileNames.Where(static n => !n.Equals("readme.txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static k => k, StringComparer.Ordinal),
            inner.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_TruncatedImage_ThrowsNamedError()
    {
        var full = File.ReadAllBytes(FixtureIsoPath);
        Assert.True(full.Length > 1024 * 1024);
        var truncatedPath = CreateTempDir("xiso_fs_trunc");
        var truncated = Path.Combine(truncatedPath, "cut.iso");
        File.WriteAllBytes(truncated, full.AsSpan(0, 700 * 1024).ToArray());

        var memory = new MemoryFilesystem();
        // The walk hits the truncated image's first out-of-range pointer — the
        // $SystemUpdate table lives past the cut — and fails with a named
        // structural error before silently unpacking garbage.
        var ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(truncated, memory));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/$SystemUpdate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_Cancellation_StopsMidRun()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        var memory = new MemoryFilesystem();

        Assert.Throws<OperationCanceledException>(() => XisoReader.UnpackImage(FixtureIsoPath, memory,
            cancellationToken: cts.Token,
            progress: new InlineProgress(info =>
            {
                if (info.Type == ProgressInfoType.FileAdded && !cancelled)
                {
                    cancelled = true;
                    // ReSharper disable once AccessToDisposedClosure — callback runs synchronously inside UnpackImage.
                    cts.Cancel();
                }
            })));

        Assert.True(cancelled);
        Assert.InRange(memory.FileNames.Count, 1, 8);
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_EmptyImage_ThrowsXisoEmpty()
    {
        var src = CreateTempDir("xiso_fs_empty_src");
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var outDir = CreateTempDir("xiso_fs_empty_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out var isoPath, null, null));

        var img = File.ReadAllBytes(isoPath!);
        Array.Clear(img, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        var bad = Path.Combine(outDir, "empty.iso");
        File.WriteAllBytes(bad, img);

        Assert.Throws<XisoEmptyException>(() => XisoReader.UnpackImage(bad, new MemoryFilesystem()));
    }

    [Fact]
    public void UnpackImage_NullFilesystem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            XisoReader.UnpackImage(FixtureIsoPath, (IFilesystem)null!));
        Assert.Throws<ArgumentNullException>(() =>
            XisoReader.UnpackImage(FixtureIsoPath, (IFilesystem)null!, CancellationToken.None));
    }

    [Fact]
    public void UnpackImage_NegativeSkipSectors_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.UnpackImage(FixtureIsoPath, new MemoryFilesystem(), skipSectors: -1));
        using var stream = File.OpenRead(FixtureIsoPath);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.UnpackImage(stream, "test_fixture.iso", new MemoryFilesystem(), skipSectors: -1));
    }
}