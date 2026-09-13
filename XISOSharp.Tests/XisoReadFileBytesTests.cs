namespace XISOSharp.Tests;

/// <summary>
/// Static span convenience (2.1): <c>XisoReader.ReadFileBytes</c> one-shot
/// reads with no extraction to disk — offset at/past EOF returns 0, a short
/// buffer or short file tail reads what exists, missing/directory targets
/// throw, and the stream overload leaves the caller's stream open.
/// </summary>
[Collection("Sequential")]
public sealed class XisoReadFileBytesTests : IDisposable
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

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso(out byte[] fileA, out byte[] fileB)
    {
        fileA = new byte[Constants.SectorSize];
        for (int i = 0; i < fileA.Length; i++) fileA[i] = (byte)(i & 0xFF);
        fileB = new byte[(3 * Constants.SectorSize) + 5];
        for (int i = 0; i < fileB.Length; i++) fileB[i] = (byte)(0x33 + (i % 64));

        string src = CreateTempDir("xiso_rfb_src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), fileA);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), fileB);
        File.WriteAllBytes(Path.Combine(src, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string outDir = CreateTempDir("xiso_rfb_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    [Fact]
    public void ReadFileBytes_FullFile_MatchesSource()
    {
        string iso = CreateIso(out _, out byte[] fileB);
        byte[] buffer = new byte[fileB.Length];

        int n = XisoReader.ReadFileBytes(iso, "/b.bin", buffer, 0);

        Assert.Equal(fileB.Length, n);
        Assert.Equal(fileB, buffer);
    }

    [Fact]
    public void ReadFileBytes_OffsetAtEof_ReturnsZero()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        byte[] buffer = new byte[64];

        Assert.Equal(0, XisoReader.ReadFileBytes(iso, "/a.bin", buffer, fileA.Length));
        Assert.Equal(0, XisoReader.ReadFileBytes(iso, "/a.bin", buffer, fileA.Length + 10_000));
    }

    [Fact]
    public void ReadFileBytes_ShortFinalChunk_Clamps()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        byte[] buffer = new byte[64];

        int n = XisoReader.ReadFileBytes(iso, "/a.bin", buffer, fileA.Length - 4);

        Assert.Equal(4, n);
        Assert.Equal(fileA.AsSpan(fileA.Length - 4, 4).ToArray(), buffer.AsSpan(0, n).ToArray());
    }

    [Fact]
    public void ReadFileBytes_OffsetInside_ReturnsRequestedBytes()
    {
        string iso = CreateIso(out _, out byte[] fileB);
        byte[] buffer = new byte[100];
        const long offset = 2048 + 3;

        int n = XisoReader.ReadFileBytes(iso, "/b.bin", buffer, offset);

        Assert.Equal(100, n);
        Assert.Equal(fileB.AsSpan((int)offset, 100).ToArray(), buffer);
    }

    [Fact]
    public void ReadFileBytes_SmallerBufferThanRemaining_ReadsBufferSize()
    {
        string iso = CreateIso(out _, out _);
        byte[] buffer = new byte[7];

        Assert.Equal(7, XisoReader.ReadFileBytes(iso, "/b.bin", buffer, 0));
    }

    [Fact]
    public void ReadFileBytes_EmptyBuffer_ReturnsZero()
    {
        string iso = CreateIso(out _, out _);
        Assert.Equal(0, XisoReader.ReadFileBytes(iso, "/b.bin", Span<byte>.Empty, 0));
    }

    [Fact]
    public void ReadFileBytes_NegativeOffset_Throws()
    {
        string iso = CreateIso(out _, out _);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XisoReader.ReadFileBytes(iso, "/b.bin", new byte[8], -1));
    }

    [Fact]
    public void ReadFileBytes_Missing_ThrowsInvalidData()
    {
        string iso = CreateIso(out _, out _);
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ReadFileBytes(iso, "/nope.bin", new byte[8], 0));
    }

    [Fact]
    public void ReadFileBytes_Directory_ThrowsInvalidData()
    {
        string iso = CreateIso(out _, out _);
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ReadFileBytes(iso, "/sub", new byte[8], 0));
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ReadFileBytes(iso, "/", new byte[8], 0));
    }

    [Fact]
    public void ReadFileBytes_EmptyFile_ReturnsZero()
    {
        string iso = CreateIso(out _, out _);
        Assert.Equal(0, XisoReader.ReadFileBytes(iso, "/empty.bin", new byte[8], 0));
    }

    [Fact]
    public void ReadFileBytes_StreamOverload_LeavesStreamOpen()
    {
        string iso = CreateIso(out _, out byte[] fileB);
        using FileStream fs = new(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] buffer = new byte[32];

        int n = XisoReader.ReadFileBytes(fs, iso, "/b.bin", buffer, 0);

        Assert.Equal(32, n);
        Assert.Equal(fileB.AsSpan(0, 32).ToArray(), buffer);
        Assert.True(fs.CanRead); // caller owns and keeps the stream
    }

    [Fact]
    public void ReadFileBytes_CaseInsensitivePath_Resolves()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        byte[] buffer = new byte[fileA.Length];

        int n = XisoReader.ReadFileBytes(iso, "/A.BIN", buffer, 0);

        Assert.Equal(fileA.Length, n);
        Assert.Equal(fileA, buffer);
    }

    [Fact]
    public void ReadFileBytes_EmptyBuffer_StillValidatesPath()
    {
        string iso = CreateIso(out _, out _);

        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ReadFileBytes(iso, "/nope.bin", Span<byte>.Empty, 0));
        Assert.Throws<InvalidDataException>(() =>
            XisoReader.ReadFileBytes(iso, "/sub", Span<byte>.Empty, 0));
        Assert.Equal(0, XisoReader.ReadFileBytes(iso, "/b.bin", Span<byte>.Empty, 0));
    }
}
