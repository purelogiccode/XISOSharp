namespace XISOSharp.Tests;

/// <summary>
/// <see cref="XisoExplorer.OpenReadStream(string)"/> over CISO containers —
/// single <c>.cso</c> and split <c>.1.cso</c> part sets must expose the same
/// bounded file streams as the source ISO (bytes, lengths, EOF clamp), proving
/// the random-access path routes through the decompressed block device.
/// </summary>
[Collection("Sequential")]
public sealed class XisoOpenReadStreamCsoTests : IDisposable
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
        fileB = new byte[(3 * Constants.SectorSize) + 11];
        for (int i = 0; i < fileB.Length; i++) fileB[i] = (byte)(0x5A ^ (i & 0xFF));

        string src = CreateTempDir("xiso_orsc_src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), fileA);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), fileB);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "cso read stream");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string outDir = CreateTempDir("xiso_orsc_iso");
        int result = XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null);
        Assert.Equal(0, result);
        Assert.NotNull(isoPath);
        return isoPath;
    }

    private static string Compress(string iso, string csoName, long? splitBytes = null)
    {
        string cso = Path.Combine(Path.GetDirectoryName(iso)!, csoName);
        int rc = CisoWriter.CompressToCso(iso, cso, level: 0, splitBytes: splitBytes);
        Assert.Equal(0, rc);
        return cso;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void SingleCso_FullReads_MatchSourceBytes()
    {
        string iso = CreateIso(out byte[] fileA, out byte[] fileB);
        string cso = Compress(iso, "game.cso");
        XisoExplorer explorer = new(cso);

        using (Stream a = explorer.OpenReadStream("/a.bin"))
        {
            Assert.Equal(fileA.Length, a.Length);
            Assert.Equal(fileA, ReadAll(a));
        }

        using (Stream b = explorer.OpenReadStream("/b.bin"))
        {
            Assert.Equal(fileB.Length, b.Length);
            Assert.Equal(fileB, ReadAll(b));
        }

        using Stream fromNode = explorer.OpenReadStream(explorer.GetNode("/readme.txt")!);
        Assert.Equal("cso read stream", System.Text.Encoding.ASCII.GetString(ReadAll(fromNode)));
    }

    [Fact]
    public void SingleCso_RandomSeeks_ClampToFileSize()
    {
        string iso = CreateIso(out byte[] fileA, out byte[] fileB);
        _ = fileA;
        string cso = Compress(iso, "game.cso");
        XisoExplorer explorer = new(cso);

        using Stream b = explorer.OpenReadStream("/b.bin");
        b.Seek(fileB.Length - 5, SeekOrigin.Begin);
        byte[] tail = new byte[64];
        int n = b.Read(tail);
        Assert.Equal(5, n);
        Assert.Equal(fileB.AsSpan(fileB.Length - 5, 5).ToArray(), tail.AsSpan(0, n).ToArray());

        b.Seek(b.Length, SeekOrigin.Begin);
        Assert.Equal(0, b.Read(new byte[32]));
    }

    [Fact]
    public void SplitCso_FirstPart_FullReadsMatchSourceBytes()
    {
        string iso = CreateIso(out byte[] fileA, out byte[] fileB);
        string csoBase = Compress(iso, "game.cso", splitBytes: 300L * 1024);
        string first = Path.ChangeExtension(csoBase, ".1.cso");
        Assert.True(File.Exists(first));

        XisoExplorer explorer = new(first);

        using (Stream a = explorer.OpenReadStream("/a.bin"))
        {
            Assert.Equal(fileA, ReadAll(a));
        }

        using (Stream b = explorer.OpenReadStream("/b.bin"))
        {
            Assert.Equal(fileB, ReadAll(b));
        }
    }

    [Fact]
    public void SingleCso_MissingAndDirectory_ThrowInvalidData()
    {
        string iso = CreateIso(out _, out _);
        string cso = Compress(iso, "game.cso");
        XisoExplorer explorer = new(cso);

        Assert.Throws<InvalidDataException>(() => explorer.OpenReadStream("/nope.bin"));
        Assert.Throws<InvalidDataException>(() => explorer.OpenReadStream("/sub"));
    }

    [Fact]
    public void SingleCso_KeepOpen_ReadStreamMatchesStateless()
    {
        string iso = CreateIso(out byte[] fileA, out _);
        string cso = Compress(iso, "game.cso");
        using XisoExplorer explorer = new(cso, new XisoExplorerOptions { KeepOpen = true });

        using Stream a = explorer.OpenReadStream("/a.bin");
        Assert.Equal(fileA, ReadAll(a));
    }
}
