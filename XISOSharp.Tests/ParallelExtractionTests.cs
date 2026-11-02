using System.Security.Cryptography;
using XISOSharp.Cli;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// PlanToEnhance §15.4/§15.6: bounded parallel extraction. Independent files are
/// queued during the sequential tree walk and copied by
/// <see cref="UnpackOptions.MaxDegreeOfParallelism"/> workers afterwards.
/// Output, resume behavior and per-file progress ordering must match a
/// sequential unpack, and failures still surface as the named
/// <see cref="ExtractFileException"/> (or the continue-on-error summary).
/// </summary>
[Collection("Sequential")]
public sealed class ParallelExtractionTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly string _savedCwd;
    private readonly bool _savedRouteInfoToError;

    public ParallelExtractionTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        _savedRouteInfoToError = Logger.RouteInfoToError;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = false;

        _savedCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(CreateTempDir("xiso_parallel_rundir"));
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_savedCwd);
        }
        catch
        {
            // ignored
        }

        Console.SetOut(_savedOut);
        Console.SetError(_savedErr);
        Logger.Out = _savedOut;
        Logger.Error = _savedErr;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        Logger.RouteInfoToError = _savedRouteInfoToError;
        _outCapture.Dispose();
        _errCapture.Dispose();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                // best effort cleanup
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

    private static UnpackOptions Parallel(int jobs) => new() { MaxDegreeOfParallelism = jobs };

    private static void WritePayload(string path, int size)
    {
        byte[] payload = new byte[size];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);
    }

    private string CreateSourceTree()
    {
        string root = CreateTempDir("xiso_parallel_src");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "big"));

        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "empty.bin"), string.Empty);
        WritePayload(Path.Combine(root, "sub", "b.bin"), 5000);
        WritePayload(Path.Combine(root, "data", "c.bin"), 20000);
        WritePayload(Path.Combine(root, "big", "d.bin"), 3_000_000);
        return root;
    }

    private string CreateIso(string srcDir, string isoName = "game.iso")
    {
        string outputDir = CreateTempDir("xiso_parallel_iso");
        string isoPath = Path.Combine(outputDir, isoName);
        int result = XisoWriter.CreateXiso(srcDir, outputDir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        Assert.Equal(isoPath, created);
        return isoPath;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using SHA256 sha = SHA256.Create();
            using FileStream fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    private static void PinTimes(string root)
    {
        DateTime pinned = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, pinned);
    }

    private sealed class CollectingProgress : IProgress<ProgressInfo>
    {
        private readonly List<ProgressInfo> _events = [];
#pragma warning disable MA0158 // object keeps net8 compat (System.Threading.Lock is net9+)
        private readonly object _gate = new();
#pragma warning restore MA0158

        internal IReadOnlyList<ProgressInfo> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public void Report(ProgressInfo value)
        {
            lock (_gate)
            {
                _events.Add(value);
            }
        }
    }

    private static string[] FileAddedPaths(CollectingProgress collector, string root)
    {
        string prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        return collector.Events
            .Where(static e => e.Type == ProgressInfoType.FileAdded)
            .Select(e =>
            {
                string path = e.Path ?? "";
                return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
            })
            .ToArray();
    }

    [Fact]
    public void ParallelUnpack_MatchesSequentialOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, sequential));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, parallel, options: Parallel(4)));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
        Assert.Equal(HashTree(src), HashTree(parallel));
    }

    [Fact]
    public void ParallelUnpack_FileAddedProgressMatchesSequentialOrder()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        CollectingProgress sequentialProgress = new();
        CollectingProgress parallelProgress = new();
        string sequentialDest = CreateTempDir("xiso_parallel_seq");
        string parallelDest = CreateTempDir("xiso_parallel_par");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, sequentialDest,
            progress: sequentialProgress));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, parallelDest,
            options: Parallel(4), progress: parallelProgress));

        string[] sequentialPaths = FileAddedPaths(sequentialProgress, sequentialDest);
        string[] parallelPaths = FileAddedPaths(parallelProgress, parallelDest);
        Assert.Equal(5, sequentialPaths.Length);
        Assert.Equal(sequentialPaths, parallelPaths);
    }

    [Fact]
    public void ParallelUnpack_IntoMemoryFilesystem_MatchesLocalOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string local = CreateTempDir("xiso_parallel_seq");
        MemoryFilesystem memory = new();

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, local));
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, memory, options: Parallel(4)));

        Dictionary<string, string> localHashes = HashTree(local);
        Assert.Equal(localHashes.Count, memory.FileNames.Count);
        foreach (string name in memory.FileNames)
        {
            string expectedPath = Path.Combine(local, name.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(File.ReadAllBytes(expectedPath), memory.ReadAllBytes(name));
        }
    }

    [Fact]
    public void ParallelUnpack_SkipExisting_ResumesAndLeavesSurvivorsUntouched()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string dest = CreateTempDir("xiso_parallel_dest");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest));
        PinTimes(dest);
        File.Delete(Path.Combine(dest, "sub", "b.bin"));
        File.Delete(Path.Combine(dest, "big", "d.bin"));

        UnpackOptions options = new() { SkipExisting = true, MaxDegreeOfParallelism = 4 };
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, dest, options: options));

        Assert.Equal(HashTree(src), HashTree(dest));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            File.GetLastWriteTimeUtc(Path.Combine(dest, "a.txt")));
        Assert.Contains("skip: ", _outCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParallelUnpack_ContinueOnError_RecordsFailuresAndExtractsRest()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string dest = CreateTempDir("xiso_parallel_dest");

        // A directory where b.bin goes: FileStream.Create fails on every OS.
        Directory.CreateDirectory(Path.Combine(dest, "sub", "b.bin"));

        UnpackOptions options = new() { ContinueOnError = true, MaxDegreeOfParallelism = 4 };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(isoPath, dest, options: options));

        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("1 file(s) failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b.bin", ex.Message, StringComparison.Ordinal);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(dest, "big", "d.bin")).Length);
    }

    [Fact]
    public void ParallelUnpack_FailFast_ThrowsNamedExtractError()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string dest = CreateTempDir("xiso_parallel_dest");
        Directory.CreateDirectory(Path.Combine(dest, "sub", "b.bin"));

        ExtractFileException ex = Assert.Throws<ExtractFileException>(() =>
            XisoReader.UnpackImage(isoPath, dest, options: Parallel(4)));

        Assert.Equal(ExtractError.ErrFileWrite, ex.ErrorCode);
        Assert.Contains("b.bin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParallelUnpack_TruncatedImage_ThrowsNamedErrorAndRemovesPartialFile()
    {
        string src = CreateTempDir("xiso_parallel_src");
        WritePayload(Path.Combine(src, "c.bin"), 20000);
        string isoPath = CreateIso(src);

        EntryInfo entry = Assert.IsType<EntryInfo>(XisoReader.GetEntryInfo(isoPath, "/c.bin"));
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        long dataEnd = ((long)entry.StartSector * Constants.SectorSize) + vol.DiscLseek + entry.FileSize;
        byte[] data = File.ReadAllBytes(isoPath);
        string cut = Path.Combine(Path.GetDirectoryName(isoPath)!, "cut_" + Path.GetFileName(isoPath));
        File.WriteAllBytes(cut, data.AsSpan(0, (int)(dataEnd - 5000)).ToArray());

        string dest = CreateTempDir("xiso_parallel_dest");
        ExtractFileException ex = Assert.Throws<ExtractFileException>(() =>
            XisoReader.UnpackImage(cut, dest, options: Parallel(4)));

        Assert.Equal(ExtractError.ErrFileTruncated, ex.ErrorCode);
        Assert.Contains("c.bin", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dest, "c.bin")));
    }

    [Fact]
    public void ParallelUnpack_MemoryStreamSource_MatchesFileOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");

        Assert.Equal(0, XisoReader.UnpackImage(isoPath, sequential));
        using MemoryStream image = new(File.ReadAllBytes(isoPath));
        Assert.Equal(0, XisoReader.UnpackImage(image, isoPath, parallel, options: Parallel(4)));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
    }

    [Fact]
    public void ParallelUnpack_CisoSource_MatchesIsoOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string csoPath = Path.Combine(CreateTempDir("xiso_parallel_cso"), "game.cso");
        Assert.Equal(0, Program.Main(["compress", isoPath, csoPath, "--ciso-split", "0", "-y"]));

        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, sequential));
        Assert.Equal(0, XisoReader.UnpackImage(csoPath, parallel, options: Parallel(4)));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
    }

    [Fact]
    public void ParallelUnpack_SplitIsoSource_MatchesJoinedOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string splitDir = CreateTempDir("xiso_parallel_split");
        IReadOnlyList<string> parts = XisoSplitter.Split(isoPath,
            Path.Combine(splitDir, "game"), 1_000_000);
        Assert.True(parts.Count > 1, "fixture must produce multiple parts");

        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, sequential));
        Assert.Equal(0, XisoReader.UnpackImage(parts[0], parallel, options: Parallel(4)));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
    }

    [Fact]
    public void ParallelUnpack_Cancellation_RemovesPartialFiles()
    {
        string src = CreateTempDir("xiso_parallel_src");
        WritePayload(Path.Combine(src, "big1.bin"), 8_000_000);
        WritePayload(Path.Combine(src, "big2.bin"), 8_000_000);
        File.WriteAllText(Path.Combine(src, "small.txt"), "hello");
        string isoPath = CreateIso(src);
        string dest = CreateTempDir("xiso_parallel_dest");

        using CancellationTokenSource cts = new();
        CancelOnFirstProgress progress = new(cts);
        Assert.ThrowsAny<OperationCanceledException>(() => XisoReader.UnpackImage(isoPath, dest,
            cancellationToken: cts.Token, options: Parallel(4), progress: progress));

        // Completed files are complete; any in-flight file was removed rather
        // than left truncated.
        foreach (string file in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dest, file);
            string expected = Path.Combine(src, rel);
            Assert.True(File.Exists(expected), $"unexpected destination file: {rel}");
            Assert.Equal(new FileInfo(expected).Length, new FileInfo(file).Length);
        }
    }

    private sealed class CancelOnFirstProgress(CancellationTokenSource cts) : IProgress<ProgressInfo>
    {
        private int _cancelled;

        public void Report(ProgressInfo value)
        {
            if (value.Type == ProgressInfoType.FileProgress && Interlocked.Exchange(ref _cancelled, 1) == 0)
                cts.Cancel();
        }
    }

    [Fact]
    public void MaxDegreeOfParallelism_OutsideRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnpackOptions { MaxDegreeOfParallelism = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UnpackOptions { MaxDegreeOfParallelism = UnpackOptions.MaxParallelism + 1 });
        Assert.Equal(UnpackOptions.MaxParallelism, new UnpackOptions
        {
            MaxDegreeOfParallelism = UnpackOptions.MaxParallelism
        }.MaxDegreeOfParallelism);
    }

    [Fact]
    public void Cli_ExtractJobs_MatchesSequentialOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");

        Assert.Equal(0, Program.Main(["-x", "-d", sequential, isoPath]));
        Assert.Equal(0, Program.Main(["-x", "-d", parallel, "--jobs", "4", isoPath]));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
    }

    [Fact]
    public void Cli_UnpackJobs_MatchesSequentialOutput()
    {
        string src = CreateSourceTree();
        string isoPath = CreateIso(src);
        string sequential = CreateTempDir("xiso_parallel_seq");
        string parallel = CreateTempDir("xiso_parallel_par");

        Assert.Equal(0, Program.Main(["--unpack", isoPath, sequential]));
        Assert.Equal(0, Program.Main(["--unpack", "--jobs", "4", isoPath, parallel]));

        Assert.Equal(HashTree(sequential), HashTree(parallel));
    }

    [Fact]
    public void Cli_JobsOutsideRange_IsRejected()
    {
        Assert.Equal(1, Program.Main(["-x", "--jobs", "0", "no-such-image.iso"]));
        Assert.Equal(1, Program.Main(["-x", "--jobs", "65", "no-such-image.iso"]));
    }

    [Fact]
    public void Cli_JobsOutsideExtract_IsRejected()
    {
        int rc = Program.Main(["--jobs", "4", "-l", "no-such-image.iso"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs is only used with --zar, extract (-x) or --unpack",
            _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_JobsWithZarUnpack_IsRejected()
    {
        int rc = Program.Main(["--unpack", "--jobs", "2", "game.zar"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs is not supported for ZArchive extraction",
            _errCapture.ToString(), StringComparison.Ordinal);

        _errCapture.GetStringBuilder().Clear();
        int rc2 = Program.Main(["-x", "--jobs", "2", "game.zar"]);

        Assert.Equal(1, rc2);
        Assert.Contains("--jobs is not supported for ZArchive extraction",
            _errCapture.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_JobsWithCopyOut_IsRejected()
    {
        int rc = Program.Main(["--copy-out", "--jobs", "2", "game.iso", "/a.txt", "out.txt"]);

        Assert.Equal(1, rc);
        Assert.Contains("--jobs is only used with --zar, extract (-x) or --unpack",
            _errCapture.ToString(), StringComparison.Ordinal);
    }
}
