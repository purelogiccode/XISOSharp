using XISOSharp.Cli;
using XISOSharp.Interfaces;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for TODO #9 (xdvdfs #187): extraction robustness.
/// A truncated image or an uncreatable destination throws
/// <see cref="ExtractFileException"/> naming the entry, its sector, and
/// expected vs actual bytes (the xdvdfs <c>Failed to create file X</c> shape),
/// instead of warning-and-continuing or hanging on a 0-byte read.
/// With <see cref="UnpackOptions.ContinueOnError"/>, per-file failures are
/// logged and skipped while the rest of the image still extracts, and the run
/// ends with an <see cref="ExtractError.ErrExtractFailed"/> summary.
/// CLI runs go through <see cref="Program.Main"/> end to end.
/// </summary>
[Collection("Sequential")]
public class ExtractRobustnessTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _outCapture = new();
    private readonly StringWriter _errCapture = new();
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedErr;
    private readonly string _savedCwd;

    public ExtractRobustnessTests()
    {
        _savedOut = Console.Out;
        _savedErr = Console.Error;
        Console.SetOut(_outCapture);
        Console.SetError(_errCapture);
        Logger.Out = _outCapture;
        Logger.Error = _errCapture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;

        _savedCwd = Directory.GetCurrentDirectory();
        string runDir = CreateTempDir("xiso_robust_rundir");
        Directory.SetCurrentDirectory(runDir);
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

    private string CreateIso(Action<string> populate, string isoName)
    {
        string src = CreateTempDir("xiso_robust_src");
        populate(src);
        string dir = CreateTempDir("xiso_robust_iso");
        int result = XisoWriter.CreateXiso(src, dir, null, null, out string? created, isoName, null);
        Assert.Equal(0, result);
        return created!;
    }

    private static void WritePayload(string path, int size)
    {
        byte[] payload = new byte[size];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);
    }

    /// <summary>
    /// Destination wrapper whose file stream fails the second write, simulating a
    /// disk that fills up mid-copy. Deletes are forwarded so partial-file cleanup
    /// can be observed on the backing filesystem.
    /// </summary>
    private sealed class FailingWriteFilesystem(IFilesystem inner) : IFilesystem
    {
        private readonly IFilesystem _inner = inner;

        public Stream CreateFile(string path) => new FailingWriteStream(_inner.CreateFile(path));

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool FileExists(string path) => _inner.FileExists(path);

        public long FileLength(string path) => _inner.FileLength(path);

        public void DeleteFile(string path) => _inner.DeleteFile(path);

        private sealed class FailingWriteStream(Stream inner) : Stream
        {
            private readonly Stream _inner = inner;
            private int _writes;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (++_writes > 1) throw new IOException("No space left on device");
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Cuts the image inside <paramref name="internalPath"/>'s data, keeping
    /// all but the last <paramref name="dropBytes"/> of that file.
    /// (Cutting the tail is not enough: created images end with zero padding.)
    /// </summary>
    private static string TruncateInsideFile(string isoPath, string internalPath, int dropBytes)
    {
        EntryInfo? entry = XisoReader.GetEntryInfo(isoPath, internalPath);
        Assert.NotNull(entry);
        VolumeInfo vol = XisoReader.GetVolumeInfo(isoPath);
        long dataEnd = ((long)entry.StartSector * Constants.SectorSize) + vol.DiscLseek + entry.FileSize;
        Assert.True(dataEnd - dropBytes > Constants.HeaderOffset,
            "fixture layout unexpectedly small; cannot truncate inside file data");

        byte[] data = File.ReadAllBytes(isoPath);
        string cut = Path.Combine(Path.GetDirectoryName(isoPath)!, "cut_" + Path.GetFileName(isoPath));
        File.WriteAllBytes(cut, data.AsSpan(0, (int)(dataEnd - dropBytes)).ToArray());
        return cut;
    }

    [Fact]
    public void TruncatedImage_Unpack_ThrowsNamedTruncationError()
    {
        string isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        string cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        string dest = CreateTempDir("xiso_robust_dest");

        // Pre-fix this hung forever: a 0-byte read at end of image never
        // advanced totalSize, so the copy loop never exited.
        ExtractFileException ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(cut, dest));
        Assert.Equal(ExtractError.ErrFileTruncated, ex.ErrorCode);
        Assert.Contains("c.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sector", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("c.bin", ex.DestPath);
        // The partial destination must not survive the failure.
        Assert.False(File.Exists(Path.Combine(dest, "c.bin")));
    }

    [Fact]
    public void WriteFailure_RemovesPartialFileFromCustomFilesystem()
    {
        string isoPath = CreateIso(src => WritePayload(Path.Combine(src, "big.bin"), 3_000_000), "game.iso");
        MemoryFilesystem memory = new();
        FailingWriteFilesystem fs = new(memory);

        ExtractFileException ex = Assert.Throws<ExtractFileException>(() =>
            XisoReader.UnpackImage(isoPath, fs));

        Assert.Equal(ExtractError.ErrFileWrite, ex.ErrorCode);
        Assert.False(memory.FileExists("big.bin"));
        Assert.DoesNotContain("big.bin", memory.FileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlockedDestination_Unpack_ThrowsWithFileContext()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        string dest = CreateTempDir("xiso_robust_dest");

        // A directory where the file goes: FileStream.Create fails on every OS.
        Directory.CreateDirectory(Path.Combine(dest, "a.txt"));

        ExtractFileException ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(isoPath, dest));
        Assert.Equal(ExtractError.ErrFileWrite, ex.ErrorCode);
        Assert.Contains("a.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sector", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void FailFast_StopsAtFirstError_NotSummary()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        string dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        ExtractFileException ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(isoPath, dest));
        Assert.NotEqual(ExtractError.ErrExtractFailed, ex.ErrorCode);
    }

    [Fact]
    public void ContinueOnError_ExtractsRest_AndSummarizes()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        string dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(isoPath, dest, options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("Failed to unpack image", ex.Message, StringComparison.Ordinal);
        Assert.Contains("game.iso", ex.Message, StringComparison.Ordinal);
        Assert.Contains("b.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 file(s) failed", ex.Message, StringComparison.Ordinal);

        // The healthy entry still extracts with identical content.
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void ContinueOnError_BlockedSubdirectory_SkipsSubtree()
    {
        string isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        string dest = CreateTempDir("xiso_robust_dest");

        // A file where the subdirectory goes: CreateDirectory fails, so the
        // whole subtree is skipped while the rest still extracts.
        File.WriteAllText(Path.Combine(dest, "sub"), "blocker");

        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(isoPath, dest, options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("sub", ex.Message, StringComparison.Ordinal);
        Assert.Equal("top", File.ReadAllText(Path.Combine(dest, "top.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "sub", "inner.txt")));
    }

    [Fact]
    public void CopyOut_Truncated_ThrowsNamedError()
    {
        string isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        string cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        string dest = Path.Combine(CreateTempDir("xiso_robust_dest"), "c.bin");

        ExtractFileException ex = Assert.Throws<ExtractFileException>(() => XisoReader.CopyOut(cut, "/c.bin", dest));
        Assert.Equal(ExtractError.ErrFileTruncated, ex.ErrorCode);
        Assert.Contains("/c.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20000", ex.Message, StringComparison.Ordinal);
        // CopyOut must remove the partial destination before rethrowing.
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void CopyOutDirectory_ContinueOnError_Summarizes()
    {
        string isoPath = CreateIso(src =>
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "world");
        }, "game.iso");
        string dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.CopyOut(isoPath, "/sub", dest, options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("b.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void Cli_ContinueOnError_ExitOne_NamesFile()
    {
        string isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        string cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        string dest = CreateTempDir("xiso_robust_cli");

        int rc = Program.Main(["-x", "-d", dest, "--continue-on-error", cut]);

        Assert.Equal(1, rc);
        string err = _errCapture.ToString();
        Assert.Contains("Failed to unpack image", err, StringComparison.Ordinal);
        Assert.Contains("c.bin", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ContinueOnError_WrongMode_Rejected()
    {
        string isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");

        int rc = Program.Main(["-t", "--continue-on-error", isoPath]);

        Assert.Equal(1, rc);
        Assert.Contains("--continue-on-error is only supported", _errCapture.ToString(), StringComparison.Ordinal);
    }
}
