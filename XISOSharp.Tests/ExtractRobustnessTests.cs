using XISOSharp.Cli;
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
    private readonly string _runDir;

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
        _runDir = CreateTempDir("xiso_robust_rundir");
        Directory.SetCurrentDirectory(_runDir);
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

        foreach (var dir in _tempDirs)
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
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(Action<string> populate, string isoName)
    {
        var src = CreateTempDir("xiso_robust_src");
        populate(src);
        var dir = CreateTempDir("xiso_robust_iso");
        var result = XisoWriter.CreateXiso(src, dir, null, null, out var created, isoName, null);
        Assert.Equal(0, result);
        return created!;
    }

    private static void WritePayload(string path, int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);
    }

    /// <summary>
    /// Cuts the image inside <paramref name="internalPath"/>'s data, keeping
    /// all but the last <paramref name="dropBytes"/> of that file.
    /// (Cutting the tail is not enough: created images end with zero padding.)
    /// </summary>
    private string TruncateInsideFile(string isoPath, string internalPath, int dropBytes)
    {
        var entry = XisoReader.GetEntryInfo(isoPath, internalPath);
        Assert.NotNull(entry);
        var vol = XisoReader.GetVolumeInfo(isoPath);
        var dataEnd = (long)entry.StartSector * Constants.SectorSize + vol.DiscLseek + entry.FileSize;
        Assert.True(dataEnd - dropBytes > Constants.HeaderOffset,
            "fixture layout unexpectedly small; cannot truncate inside file data");

        var data = File.ReadAllBytes(isoPath);
        var cut = Path.Combine(Path.GetDirectoryName(isoPath)!, "cut_" + Path.GetFileName(isoPath));
        File.WriteAllBytes(cut, data.AsSpan(0, (int)(dataEnd - dropBytes)).ToArray());
        return cut;
    }

    [Fact]
    public void TruncatedImage_Unpack_ThrowsNamedTruncationError()
    {
        var isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        var cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        var dest = CreateTempDir("xiso_robust_dest");

        // Pre-fix this hung forever: a 0-byte read at end of image never
        // advanced totalSize, so the copy loop never exited.
        var ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(cut, dest));
        Assert.Equal(ExtractError.ErrFileTruncated, ex.ErrorCode);
        Assert.Contains("c.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sector", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("c.bin", ex.DestPath);
    }

    [Fact]
    public void BlockedDestination_Unpack_ThrowsWithFileContext()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");
        var dest = CreateTempDir("xiso_robust_dest");

        // A directory where the file goes: FileStream.Create fails on every OS.
        Directory.CreateDirectory(Path.Combine(dest, "a.txt"));

        var ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(isoPath, dest));
        Assert.Equal(ExtractError.ErrFileWrite, ex.ErrorCode);
        Assert.Contains("a.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sector", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void FailFast_StopsAtFirstError_NotSummary()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        var ex = Assert.Throws<ExtractFileException>(() => XisoReader.UnpackImage(isoPath, dest));
        Assert.NotEqual(ExtractError.ErrExtractFailed, ex.ErrorCode);
    }

    [Fact]
    public void ContinueOnError_ExtractsRest_AndSummarizes()
    {
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        }, "game.iso");
        var dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        var options = new UnpackOptions { ContinueOnError = true };
        var ex = Assert.Throws<ExtractErrorException>(() =>
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
        var isoPath = CreateIso(src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        }, "game.iso");
        var dest = CreateTempDir("xiso_robust_dest");

        // A file where the subdirectory goes: CreateDirectory fails, so the
        // whole subtree is skipped while the rest still extracts.
        File.WriteAllText(Path.Combine(dest, "sub"), "blocker");

        var options = new UnpackOptions { ContinueOnError = true };
        var ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.UnpackImage(isoPath, dest, options: options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("sub", ex.Message, StringComparison.Ordinal);
        Assert.Equal("top", File.ReadAllText(Path.Combine(dest, "top.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "sub", "inner.txt")));
    }

    [Fact]
    public void CopyOut_Truncated_ThrowsNamedError()
    {
        var isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        var cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        var dest = Path.Combine(CreateTempDir("xiso_robust_dest"), "c.bin");

        var ex = Assert.Throws<ExtractFileException>(() => XisoReader.CopyOut(cut, "/c.bin", dest));
        Assert.Equal(ExtractError.ErrFileTruncated, ex.ErrorCode);
        Assert.Contains("/c.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("20000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyOutDirectory_ContinueOnError_Summarizes()
    {
        var isoPath = CreateIso(src =>
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "world");
        }, "game.iso");
        var dest = CreateTempDir("xiso_robust_dest");
        Directory.CreateDirectory(Path.Combine(dest, "b.txt"));

        var options = new UnpackOptions { ContinueOnError = true };
        var ex = Assert.Throws<ExtractErrorException>(() =>
            XisoReader.CopyOut(isoPath, "/sub", dest, options));
        Assert.Equal(ExtractError.ErrExtractFailed, ex.ErrorCode);
        Assert.Contains("b.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void Cli_ContinueOnError_ExitOne_NamesFile()
    {
        var isoPath = CreateIso(src => WritePayload(Path.Combine(src, "c.bin"), 20000), "game.iso");
        var cut = TruncateInsideFile(isoPath, "/c.bin", 5000);
        var dest = CreateTempDir("xiso_robust_cli");

        var rc = Program.Main(["-x", "-d", dest, "--continue-on-error", cut]);

        Assert.Equal(1, rc);
        var err = _errCapture.ToString();
        Assert.Contains("Failed to unpack image", err, StringComparison.Ordinal);
        Assert.Contains("c.bin", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ContinueOnError_WrongMode_Rejected()
    {
        var isoPath = CreateIso(src => File.WriteAllText(Path.Combine(src, "a.txt"), "hello"), "game.iso");

        var rc = Program.Main(["-t", "--continue-on-error", isoPath]);

        Assert.Equal(1, rc);
        Assert.Contains("--continue-on-error is only supported", _errCapture.ToString(), StringComparison.Ordinal);
    }
}
