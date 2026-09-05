using ZARSharp.Seekable;

namespace ZARSharp.Tests;

/// <summary>
/// Committed seekable ground truth (<c>Goldens/seekable/</c>), emitted by the
/// reference C library (<c>zstd-1.5.7/contrib/seekable_format</c>,
/// <c>ZSTD_seekable_initCStream</c> level 3, 8 KiB uncompressed frames,
/// table checksums on). Note the C library writes plain zstd frames while
/// zeekstd (our port's parity target) sets the frame content-checksum flag
/// too — both are valid files and our reader handles both. These tests pin
/// the C flavor with no toolchain: full decode, cross-frame subranges and
/// frame-slice reads.
/// </summary>
public sealed class SeekableGoldenTests
{
    private static readonly byte[] Phrase =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. "u8.ToArray();

    private static string GoldensDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_XISOSharp.sln")))
            {
                return Path.Combine(dir, "ZARSharp.Tests", "Goldens");
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    private static byte[] CycleText(int n)
    {
        var buf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            buf[i] = Phrase[i % Phrase.Length];
        }

        return buf;
    }

    [Fact]
    public void NativeCFlavor_Text200k_DecodesFully()
    {
        var dir = GoldensDir();
        var expected = CycleText(200000);
        var bytes = File.ReadAllBytes(Path.Combine(dir, "seekable", "text200k_f8k.seekable"));

        var reader = new SeekableReader(bytes);
        Assert.Equal(25, reader.FrameCount);
        Assert.Equal(expected, reader.DecompressAll());
    }

    [Fact]
    public void NativeCFlavor_Text200k_Subranges()
    {
        var dir = GoldensDir();
        var expected = CycleText(200000);
        var bytes = File.ReadAllBytes(Path.Combine(dir, "seekable", "text200k_f8k.seekable"));
        var reader = new SeekableReader(bytes);

        // Inside the first frame, straddling a frame boundary, and the tail.
        Assert.Equal(expected[..8192], reader.DecompressRange(0, 8192));
        Assert.Equal(expected[8000..12000], reader.DecompressRange(8000, 4000));
        Assert.Equal(expected[199000..], reader.DecompressRange(199000, 1000));
        Assert.Equal(expected[..8192], reader.DecompressFrames(0, 0));
        Assert.Equal(expected.AsSpan(3 * 8192, 8192).ToArray(), reader.DecompressFrames(3, 3));
    }

    [Fact]
    public void NativeCFlavor_Hetero64_DecodesFully()
    {
        var dir = GoldensDir();
        var expected = File.ReadAllBytes(Path.Combine(dir, "zstd", "hetero64.bin"));
        var bytes = File.ReadAllBytes(Path.Combine(dir, "seekable", "hetero64_f8k.seekable"));

        var reader = new SeekableReader(bytes);
        // The C library ends the exactly-full 8th frame during Write and
        // appends an empty 9th at endStream; zeekstd (our writer's model)
        // logs the full frame once at Finish instead. Same content either way.
        Assert.Equal(9, reader.FrameCount);
        Assert.Equal(expected, reader.DecompressAll());
        Assert.Equal([], reader.DecompressFrames(8, 8));
        Assert.Equal(expected.AsSpan(8190, 8).ToArray(), reader.DecompressRange(8190, 8));
    }
}
