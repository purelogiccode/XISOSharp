using CCISharp;

namespace XISOSharp.Tests;

/// <summary>
/// Detection and open tests for <see cref="XisoImageSource"/>: every supported
/// container/part combination must classify and read identically to the source
/// ISO.
/// </summary>
[Collection("Sequential")]
public sealed class XisoImageSourceTests : IDisposable
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

    [Fact]
    public void Detect_ClassifiesEveryFormatAndLength()
    {
        string work = CreateTempDir("xiso_source");
        string iso = CreateIso(work);
        long isoLength = new FileInfo(iso).Length;
        long padded = (isoLength + 2047) / 2048 * 2048;

        string xiso = Path.Combine(work, "plain.xiso");
        File.Copy(iso, xiso);
        string img = Path.Combine(work, "plain.img");
        File.Copy(iso, img);

        string cso = Path.Combine(work, "single.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, splitBytes: null));

        string splitCsoDir = Path.Combine(work, "splitcso");
        Directory.CreateDirectory(splitCsoDir);
        string splitCso = Path.Combine(splitCsoDir, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, splitCso, splitBytes: 1 << 20));

        string cci = Path.Combine(work, "single.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, splitBytes: 0));

        string splitCciDir = Path.Combine(work, "splitcci");
        Directory.CreateDirectory(splitCciDir);
        string splitCci = Path.Combine(splitCciDir, "game.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, splitCci, splitBytes: 1 << 20));

        string splitIsoDir = Path.Combine(work, "splitiso");
        Directory.CreateDirectory(splitIsoDir);
        string splitIso = Path.Combine(splitIsoDir, "game.iso");
        XisoSplitter.Split(iso, splitIso, 1 << 20);

        AssertDetected(iso, XisoImageFormat.Iso, isoLength);
        AssertDetected(xiso, XisoImageFormat.Iso, isoLength);
        AssertDetected(img, XisoImageFormat.Iso, isoLength);
        AssertDetected(cso, XisoImageFormat.Ciso, isoLength);
        AssertDetected(Path.ChangeExtension(splitCso, "1.cso"), XisoImageFormat.SplitCiso, isoLength);
        AssertDetected(cci, XisoImageFormat.Cci, padded);
        AssertDetected(Path.ChangeExtension(splitCci, "1.cci"), XisoImageFormat.SplitCci, padded);
        AssertDetected(Path.ChangeExtension(splitIso, "1.iso"), XisoImageFormat.SplitIso, isoLength);
    }

    [Fact]
    public void OpenRead_Cci_MatchesIsoBytes()
    {
        string work = CreateTempDir("xiso_source");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, splitBytes: 0));

        byte[] expected = File.ReadAllBytes(iso);
        using Stream source = XisoImageSource.OpenRead(cci);
        byte[] actual = new byte[source.Length];

        int total = 0;
        while (total < actual.Length)
        {
            int read = source.Read(actual, total, actual.Length - total);
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(actual.Length, total);
        Assert.Equal(expected, actual.AsSpan(0, expected.Length).ToArray());
        Assert.True(actual.AsSpan(expected.Length).ToArray().All(static value => value == 0));
    }

    [Fact]
    public void OpenRead_SplitIso_SeeksAcrossPartBoundary()
    {
        string work = CreateTempDir("xiso_source");
        string iso = CreateIso(work);
        byte[] expected = File.ReadAllBytes(iso);

        string splitDir = Path.Combine(work, "split");
        Directory.CreateDirectory(splitDir);
        string splitBase = Path.Combine(splitDir, "game.iso");
        IReadOnlyList<string> parts = XisoSplitter.Split(iso, splitBase, 512 * 1024);
        Assert.True(parts.Count >= 2, "Test needs at least two parts.");

        using Stream source = XisoImageSource.OpenRead(parts[0]);
        Assert.Equal(expected.Length, source.Length);

        int offset = (int)new FileInfo(parts[0]).Length - 300;
        source.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[900];
        int read = source.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);
        Assert.Equal(expected.AsSpan(offset, buffer.Length).ToArray(), buffer);
    }

    [Fact]
    public void Detect_RenamedCci_UsesMagic()
    {
        string work = CreateTempDir("xiso_source");
        string iso = CreateIso(work);
        string cci = Path.Combine(work, "game.cci");
        Assert.Equal(0, XisoCci.CompressToCci(iso, cci, splitBytes: 0));

        string renamed = Path.Combine(work, "game.cci.bin");
        File.Move(cci, renamed);

        Assert.True(XisoImageSource.TryDetect(renamed, out XisoImageSourceInfo? info));
        Assert.NotNull(info);
        Assert.Equal(XisoImageFormat.Cci, info.Format);

        using Stream source = XisoImageSource.OpenRead(renamed);
        Assert.Equal(new FileInfo(iso).Length, source.Length);
    }

    [Theory]
    [InlineData("game.iso", "game")]
    [InlineData("game.1.iso", "game")]
    [InlineData("game.xiso", "game")]
    [InlineData("game.img", "game")]
    [InlineData("game.cso", "game")]
    [InlineData("game.1.cso", "game")]
    [InlineData("game.cci", "game")]
    [InlineData("game.1.cci", "game")]
    [InlineData("game.2.cci", "game")]
    [InlineData("game.9.cci", "game")]
    [InlineData("game.10.cci", "game.10")]
    [InlineData("game.0.cci", "game.0")]
    [InlineData("Halo.2.iso", "Halo.2")]
    [InlineData("archive.1.zar", "archive.1")]
    [InlineData("game.1.xiso", "game.1")]
    [InlineData("game.1.img", "game.1")]
    [InlineData("game", "game")]
    public void StripImageSuffix_HandlesContainersAndParts(string name, string expected) =>
        Assert.Equal(expected, XisoImageSource.StripImageSuffix(name));

    [Fact]
    public void Detect_MissingFile_ReturnsFalse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.cci");
        Assert.False(XisoImageSource.TryDetect(path, out _));
        Assert.False(XisoImageSource.IsSupportedImage(path));
    }

    [Fact]
    public void DefaultOutputNaming_StripsContainerAndPartMarkers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xiso_names");
        Assert.Equal(Path.Combine(dir, "game.cso"),
            CisoWriter.DeriveDefaultCsoPath(Path.Combine(dir, "game.1.iso"), isDir: false));
        Assert.Equal(Path.Combine(dir, "game.cci"),
            XisoCci.DeriveDefaultCciPath(Path.Combine(dir, "game.1.iso"), isDirectory: false));
        Assert.Equal(Path.Combine(dir, "game.iso"),
            XisoCci.DeriveDefaultIsoPath(Path.Combine(dir, "game.1.cci")));
        Assert.Equal(Path.Combine(dir, "game.cci"),
            XisoCci.DeriveDefaultCciPath(Path.Combine(dir, "game.2.cci"), isDirectory: false));
        Assert.Equal(Path.Combine(dir, "game.iso"),
            XisoCci.DeriveDefaultIsoPath(Path.Combine(dir, "game.2.cci")));
        Assert.Equal(Path.Combine(dir, "game.cso"),
            CisoWriter.DeriveDefaultCsoPath(Path.Combine(dir, "game.2.cci"), isDir: false));
        Assert.Equal(Path.Combine(dir, "game.iso"),
            CisoReader.DeriveDefaultIsoPath(Path.Combine(dir, "game.1.cso")));
    }

    private static void AssertDetected(string path, XisoImageFormat format, long minimumLength)
    {
        Assert.True(XisoImageSource.TryDetect(path, out XisoImageSourceInfo? info), path);
        Assert.NotNull(info);
        Assert.Equal(format, info.Format);
        Assert.True(info.Length >= minimumLength, $"{path}: {info.Length} < {minimumLength}");
        Assert.NotEmpty(info.Parts);
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string CreateIso(string work)
    {
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.txt"), new string('x', 3000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);
        return isoPath;
    }
}
