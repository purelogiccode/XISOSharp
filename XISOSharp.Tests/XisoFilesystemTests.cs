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
        foreach (string dir in _tempDirs)
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
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
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
        string root = CreateTempDir("xiso_fs_root");
        LocalFilesystem fs = new(root);
        fs.CreateDirectory("sub");

        using (Stream s = fs.CreateFile("sub/a.bin"))
        {
            s.Write([1, 2, 3, 4]);
        }

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(root, "sub", "a.bin")));
    }

    [Fact]
    public void LocalFilesystem_CreateFile_MissingDirectory_Throws()
    {
        string root = CreateTempDir("xiso_fs_root");
        LocalFilesystem fs = new(root);

        Assert.Throws<DirectoryNotFoundException>(() => fs.CreateFile("no/such/dir/f.bin"));
    }

    [Fact]
    public void LocalFilesystem_ExistsAndLength_MissingReturnsMinusOne()
    {
        string root = CreateTempDir("xiso_fs_root");
        LocalFilesystem fs = new(root);
        string path = Path.Combine(root, "f.bin");
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
        string root = CreateTempDir("xiso_fs_root");
        LocalFilesystem fs = new(root);
        fs.CreateDirectory("/d1");

        using (Stream s = fs.CreateFile("/d1/f.txt"))
        {
            s.Write([1, 1]);
        }

        // Re-creating via the other spelling truncates the same file.
        using (Stream s = fs.CreateFile("d1\\f.txt"))
        {
            s.Write([2]);
        }

        Assert.Equal([2], File.ReadAllBytes(Path.Combine(root, "d1", "f.txt")));
        Assert.Equal(1, fs.FileLength("d1/f.txt"));
    }

    [Fact]
    public void LocalFilesystem_NullRoot_ResolvesAgainstCurrentDirectory()
    {
        string cwd = CreateTempDir("xiso_fs_cwd");
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(cwd);
            LocalFilesystem fs = LocalFilesystem.Instance;

            using (Stream s = fs.CreateFile("rel.bin"))
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
        string root = CreateTempDir("xiso_fs_root");
        LocalFilesystem fs = new(root);

        Assert.False(fs.FileExists("../evil.bin"));
        Assert.Equal(-1, fs.FileLength("../evil.bin"));

        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateDirectory("../evil"));
        Assert.Throws<UnauthorizedAccessException>(() => fs.CreateFile("../evil.bin"));

        string parent = Path.GetDirectoryName(root)!;
        Assert.False(File.Exists(Path.Combine(parent, "evil.bin")));
        Assert.False(Directory.Exists(Path.Combine(parent, "evil")));
    }

    // ------------------------------------------------------------------
    // MemoryFilesystem
    // ------------------------------------------------------------------

    [Fact]
    public void MemoryFilesystem_CommitsOnDispose_RecreateReplaces()
    {
        MemoryFilesystem fs = new();

        using (Stream s = fs.CreateFile("a.bin"))
        {
            s.Write([1, 2, 3, 4, 5]);
        }

        Assert.Equal([1, 2, 3, 4, 5], fs.ReadAllBytes("a.bin"));
        Assert.Equal(5, fs.FileLength("a.bin"));

        using (Stream s = fs.CreateFile("a.bin"))
        {
            s.Write([9]);
        }

        Assert.Equal([9], fs.ReadAllBytes("a.bin"));
        Assert.Equal(1, fs.FileLength("a.bin"));
    }

    [Fact]
    public void MemoryFilesystem_AutoCreatesParents_AndTracksDirectories()
    {
        MemoryFilesystem fs = new();

        using (Stream s = fs.CreateFile("/x/y/z.bin"))
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
        MemoryFilesystem fs = new();
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
        MemoryFilesystem fs = new();
        Assert.False(fs.FileExists("nope.bin"));
        Assert.Equal(-1, fs.FileLength("nope.bin"));
    }

    [Fact]
    public void MemoryFilesystem_CreateFileAtRoot_Throws()
    {
        MemoryFilesystem fs = new();
        Assert.Throws<ArgumentException>(() => fs.CreateFile("/"));
    }

    // ------------------------------------------------------------------
    // UnpackOptions.ShouldSkip filesystem overload
    // ------------------------------------------------------------------

    [Fact]
    public void ShouldSkip_FilesystemOverload_MatchesMemorySemantics()
    {
        MemoryFilesystem fs = new();
        using (Stream s = fs.CreateFile("a.bin"))
        {
            s.Write(new byte[10]);
        }

        UnpackOptions skip = new() { SkipExisting = true };
        Assert.True(skip.ShouldSkip("a.bin", 10, fs));
        Assert.False(skip.ShouldSkip("a.bin", 11, fs));
        Assert.False(skip.ShouldSkip("missing.bin", 10, fs));
        Assert.False(skip.ShouldSkip("", 10, fs));

        UnpackOptions noSkip = new();
        Assert.False(noSkip.ShouldSkip("a.bin", 10, fs));
    }

    [Fact]
    public void ShouldSkip_LegacyOverload_StillProbesDisk()
    {
        string dir = CreateTempDir("xiso_fs_skip");
        string path = Path.Combine(dir, "on_disk.bin");
        File.WriteAllBytes(path, new byte[4]);

        UnpackOptions skip = new() { SkipExisting = true };
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
        Dictionary<string, byte[]> hashes = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            hashes[NormalizeRel(Path.GetRelativePath(root, file))] = SHA256.HashData(File.ReadAllBytes(file));
        }

        return hashes;
    }

    private static HashSet<string> DiskDirectories(string root) =>
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => NormalizeRel(Path.GetRelativePath(root, d)))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void UnpackImage_MemoryFilesystem_MatchesDiskUnpack()
    {
        Assert.True(File.Exists(FixtureIsoPath), $"Reference fixture missing: {FixtureIsoPath}");

        MemoryFilesystem memory = new();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, memory));

        string diskDir = CreateTempDir("xiso_fs_disk");
        Assert.Equal(0, XisoReader.Extract(FixtureIsoPath, diskDir, false));

        Dictionary<string, byte[]> expectedFiles = HashDiskTree(diskDir);
        Assert.NotEmpty(expectedFiles);
        Assert.Equal(
            expectedFiles.Keys.OrderBy(static k => k, StringComparer.Ordinal),
            memory.FileNames.OrderBy(static k => k, StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach ((string name, byte[] hash) in expectedFiles)
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
        MemoryFilesystem byPath = new();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, byPath));

        MemoryFilesystem byStream = new();
        using (FileStream stream = File.OpenRead(FixtureIsoPath))
        {
            Assert.Equal(0, XisoReader.UnpackImage(stream, "test_fixture.iso", byStream));
        }

        Assert.Equal(
            byPath.FileNames.OrderBy(static k => k, StringComparer.Ordinal),
            byStream.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
        foreach (string name in byPath.FileNames)
        {
            Assert.True(byPath.ReadAllBytes(name).SequenceEqual(byStream.ReadAllBytes(name)),
                $"stream overload content mismatch: {name}");
        }
    }

    [Fact]
    public void UnpackImage_LocalFilesystem_MatchesLegacyUnpack_AndNeverChangesCwd()
    {
        string cwdBefore = Directory.GetCurrentDirectory();

        string root = CreateTempDir("xiso_fs_local_root");
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, new LocalFilesystem(root)));
        Assert.Equal(cwdBefore, Directory.GetCurrentDirectory());

        string legacy = CreateTempDir("xiso_fs_local_legacy");
        Assert.Equal(0, XisoReader.Extract(FixtureIsoPath, legacy, false));

        Assert.Equal(
            HashDiskTree(root).Keys.OrderBy(static k => k, StringComparer.Ordinal),
            HashDiskTree(legacy).Keys.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_ReportsProgress()
    {
        MemoryFilesystem memory = new();
        List<ProgressInfo> added = new();
        List<ProgressInfo> chunks = new();

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
        Dictionary<string, ProgressInfo> lastPerPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProgressInfo chunk in chunks)
        {
            Assert.NotNull(chunk.Path);
            Assert.True(chunk.Size <= chunk.Count, $"progress overshoot for {chunk.Path}");
            if (lastPerPath.TryGetValue(chunk.Path!, out ProgressInfo previous))
            {
                Assert.True(chunk.Size > previous.Size, $"non-monotonic progress for {chunk.Path}");
            }

            lastPerPath[chunk.Path!] = chunk;
        }

        foreach (string name in memory.FileNames.Where(n => memory.FileLength(n) > 0))
        {
            string internalPath = "/" + name;
            Assert.True(lastPerPath.ContainsKey(internalPath), $"no FileProgress events for {internalPath}");
            Assert.Equal(memory.FileLength(name), lastPerPath[internalPath].Count);
            Assert.Equal(memory.FileLength(name), lastPerPath[internalPath].Size);
        }

        Assert.DoesNotContain(lastPerPath.Keys, static p => p.EndsWith("empty.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_SkipExisting_Resumes()
    {
        MemoryFilesystem memory = new();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, memory));
        HashSet<string> fullNames = memory.FileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Same-size placeholders stay untouched; the whole tree re-lists.
        MemoryFilesystem resumed = new();
        using (Stream s = resumed.CreateFile("readme.txt"))
        {
            s.Write(new byte[(int)memory.FileLength("readme.txt")]);
        }

        UnpackOptions options = new() { SkipExisting = true };
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, resumed, options: options));
        Assert.Equal(fullNames.OrderBy(static k => k, StringComparer.Ordinal),
            resumed.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
        Assert.Equal(new byte[(int)memory.FileLength("readme.txt")], resumed.ReadAllBytes("readme.txt"));

        // Without SkipExisting the placeholder is overwritten with real content.
        MemoryFilesystem rewritten = new();
        using (Stream s = rewritten.CreateFile("readme.txt"))
        {
            s.Write(new byte[(int)memory.FileLength("readme.txt")]);
        }

        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, rewritten));
        Assert.True(memory.ReadAllBytes("readme.txt").SequenceEqual(rewritten.ReadAllBytes("readme.txt")));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_ContinueOnError_RecordsFailureAndContinues()
    {
        MemoryFilesystem inner = new();
        ThrowingFilesystem failing = new(inner, "/readme.txt");

        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(FixtureIsoPath, failing,
                options: new UnpackOptions { ContinueOnError = true }));
        Assert.Contains("readme.txt", ex.Message, StringComparison.Ordinal);

        // Every other file still landed in the inner filesystem.
        MemoryFilesystem healthy = new();
        Assert.Equal(0, XisoReader.UnpackImage(FixtureIsoPath, healthy));
        Assert.Equal(
            healthy.FileNames.Where(static n => !n.Equals("readme.txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static k => k, StringComparer.Ordinal),
            inner.FileNames.OrderBy(static k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_TruncatedImage_ThrowsNamedError()
    {
        byte[] full = File.ReadAllBytes(FixtureIsoPath);
        Assert.True(full.Length > 1024 * 1024);
        string truncatedPath = CreateTempDir("xiso_fs_trunc");
        string truncated = Path.Combine(truncatedPath, "cut.iso");
        File.WriteAllBytes(truncated, full.AsSpan(0, 700 * 1024).ToArray());

        MemoryFilesystem memory = new();
        // The walk hits the truncated image's first out-of-range pointer — the
        // $SystemUpdate table lives past the cut — and fails with a named
        // structural error before silently unpacking garbage.
        XisoFormatException ex = Assert.Throws<XisoFormatException>(() => XisoReader.UnpackImage(truncated, memory));
        Assert.Contains("invalid TOC entry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/$SystemUpdate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnpackImage_MemoryFilesystem_Cancellation_StopsMidRun()
    {
        using CancellationTokenSource cts = new();
        bool cancelled = false;
        MemoryFilesystem memory = new();

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
        string src = CreateTempDir("xiso_fs_empty_src");
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        string outDir = CreateTempDir("xiso_fs_empty_out");
        Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null));

        byte[] img = File.ReadAllBytes(isoPath!);
        Array.Clear(img, Constants.HeaderOffset + Constants.HeaderDataLength, 8);
        string bad = Path.Combine(outDir, "empty.iso");
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
        using FileStream stream = File.OpenRead(FixtureIsoPath);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.UnpackImage(stream, "test_fixture.iso", new MemoryFilesystem(), skipSectors: -1));
    }
}
