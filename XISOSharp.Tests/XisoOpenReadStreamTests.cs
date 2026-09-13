namespace XISOSharp.Tests;

/// <summary>
/// <see cref="XisoExplorer.OpenReadStream(string)"/> / overloads (2.1): full
/// reads equal source bytes, random-offset seeks, EOF clamping to the entry's
/// <c>FileSize</c> (not the image end), directory targets rejected, and the
/// stream's lifetime tied to the explorer that produced it.
/// </summary>
[Collection("Sequential")]
public sealed class XisoOpenReadStreamTests : IDisposable
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

    /// <summary>
    /// Golden image with a one-sector file, a multi-sector file, an empty file,
    /// and a subdirectory. Sector padding makes <c>a.bin</c> and <c>b.bin</c>
    /// adjacent, so an over-read past a file's size would hit real image bytes.
    /// </summary>
    private string CreateIso(out byte[] fileA, out byte[] fileB)
    {
        fileA = new byte[Constants.SectorSize];
        for (int i = 0; i < fileA.Length; i++) fileA[i] = (byte)(i & 0xFF);
        fileB = new byte[(4 * Constants.SectorSize) + 7];
        for (int i = 0; i < fileB.Length; i++) fileB[i] = (byte)(0xA0 + (i % 32));

        string src = CreateTempDir("xiso_ors_src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), fileA);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), fileB);
        File.WriteAllBytes(Path.Combine(src, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string outDir = CreateTempDir("xiso_ors_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void OpenReadStream_FullRead_MatchesSourceBytes()
    {
        string iso = CreateIso(out byte[] fileA, out byte[] fileB);
        XisoExplorer explorer = new(iso);

        using (Stream a = explorer.OpenReadStream("/a.bin"))
        {
            Assert.True(a.CanRead);
            Assert.True(a.CanSeek);
            Assert.False(a.CanWrite);
            Assert.Equal(fileA.Length, a.Length);
            Assert.Equal(fileA, ReadAll(a));
        }

        using (Stream b = explorer.OpenReadStream("/b.bin"))
        {
            Assert.Equal(fileB.Length, b.Length);
            Assert.Equal(fileB, ReadAll(b));
        }
    }

    [Fact]
    public void OpenReadStream_Seek_RandomOffsetsMatchSource()
    {
        string iso = CreateIso(out byte[] fileA, out byte[] fileB);
        _ = fileA;
        XisoExplorer explorer = new(iso);
        using Stream b = explorer.OpenReadStream("/b.bin");

        long[] offsets = [0, 1, 1024, 2048, 3000, fileB.Length - 1, fileB.Length];
        foreach (long offset in offsets)
        {
            b.Seek(offset, SeekOrigin.Begin);
            Assert.Equal(offset, b.Position);
            int expected = (int)Math.Min(100, fileB.Length - offset);
            byte[] read = new byte[100];
            int n = b.Read(read);
            Assert.Equal(expected, n);
            Assert.Equal(fileB.AsSpan((int)offset, expected).ToArray(), read.AsSpan(0, n).ToArray());
        }
    }

    [Fact]
    public void OpenReadStream_SeekOriginCurrentAndEnd_MatchStreamConventions()
    {
        string iso = CreateIso(out _, out byte[] fileB);
        XisoExplorer explorer = new(iso);
        using Stream b = explorer.OpenReadStream("/b.bin");

        Assert.Equal(10, b.Seek(10, SeekOrigin.Begin));
        Assert.Equal(15, b.Seek(5, SeekOrigin.Current));
        Assert.Equal(fileB.Length - 3, b.Seek(-3, SeekOrigin.End));
    }

    [Fact]
    public void OpenReadStream_ReadPastFileEnd_ReturnsZero_NotNextFileBytes()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        XisoExplorer explorer = new(iso);
        using Stream a = explorer.OpenReadStream("/a.bin");

        // Exactly at the end: 0 bytes per Stream conventions.
        a.Seek(a.Length, SeekOrigin.Begin);
        Assert.Equal(0, a.Read(new byte[64]));

        // A read that starts just before the end must clamp to the file size,
        // even though the image continues with b.bin's sectors.
        a.Seek(fileA.Length - 4, SeekOrigin.Begin);
        byte[] tail = new byte[64];
        int n = a.Read(tail);
        Assert.Equal(4, n);
        Assert.Equal(fileA.AsSpan(fileA.Length - 4, 4).ToArray(), tail.AsSpan(0, n).ToArray());

        // Seeking past the end then reading is still 0.
        a.Seek(fileA.Length + 4096, SeekOrigin.Begin);
        Assert.Equal(0, a.Read(new byte[64]));
    }

    [Fact]
    public void OpenReadStream_ExplorerNodeOverload_MatchPathOverload()
    {
        string iso = CreateIso(out _, out byte[] fileB);
        XisoExplorer explorer = new(iso);

        ExplorerNode node = explorer.GetNode("/b.bin")!;
        using Stream fromNode = explorer.OpenReadStream(node);
        using Stream fromPath = explorer.OpenReadStream("/b.bin");

        Assert.Equal(fromPath.Length, fromNode.Length);
        Assert.Equal(fileB, ReadAll(fromNode));
    }

    [Fact]
    public void OpenReadStream_EmptyFile_LengthZeroAndReadsZero()
    {
        string iso = CreateIso(out _, out _);
        XisoExplorer explorer = new(iso);

        using Stream empty = explorer.OpenReadStream("/empty.bin");

        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.Read(new byte[16]));
    }

    [Fact]
    public void OpenReadStream_Missing_ThrowsInvalidData()
    {
        string iso = CreateIso(out _, out _);
        XisoExplorer explorer = new(iso);

        Assert.Throws<InvalidDataException>(() => explorer.OpenReadStream("/nope.bin"));
    }

    [Fact]
    public void OpenReadStream_Directory_ThrowsInvalidData()
    {
        string iso = CreateIso(out _, out _);
        XisoExplorer explorer = new(iso);

        Assert.Throws<InvalidDataException>(() => explorer.OpenReadStream("/"));
        Assert.Throws<InvalidDataException>(() => explorer.OpenReadStream("/sub"));
        Assert.Throws<ArgumentException>(() => explorer.OpenReadStream(explorer.GetNode("/sub")!));
    }

    [Fact]
    public void OpenReadStream_CaseInsensitivePath_Resolves()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        XisoExplorer explorer = new(iso);

        using Stream a = explorer.OpenReadStream("/A.BIN");
        Assert.Equal(fileA, ReadAll(a));
    }

    [Fact]
    public void OpenReadStream_StatelessStreamOwnsLifetime()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        XisoExplorer explorer = new(iso);

        // Disposing the explorer must not kill a stateless read stream; the
        // stream owns the image handle, not the explorer.
        Stream a = explorer.OpenReadStream("/a.bin");
        explorer.Dispose();
        try
        {
            Assert.Equal(fileA, ReadAll(a));
        }
        finally
        {
            a.Dispose();
        }
    }

    [Fact]
    public void OpenReadStream_DisposedStream_Throws()
    {
        string iso = CreateIso(out _, out _);
        XisoExplorer explorer = new(iso);
        Stream a = explorer.OpenReadStream("/a.bin");
        a.Dispose();

        Assert.Throws<ObjectDisposedException>(() => a.Read(new byte[4]));
        Assert.Throws<ObjectDisposedException>(() => a.Seek(0, SeekOrigin.Begin));
    }
}
