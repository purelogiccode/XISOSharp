using System.Security.Cryptography;
using XISOSharp.Cli;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for <c>XisoSkeleton.Apply</c> — the inverse of
/// <see cref="XisoSkeleton.Petrify"/>.
/// </summary>
[Collection("Sequential")]
public sealed class XisoSkeletonApplyTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public XisoSkeletonApplyTests()
    {
        Logger.Quiet = true;
        Logger.RealQuiet = false;
    }

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private string TempFile(string extension = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiso_apply_{Guid.NewGuid():N}{extension}");
        _tempPaths.Add(path);
        return path;
    }

    private string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiso_apply_src_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempPaths.Add(path);
        return path;
    }

    private static void Populate(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "skeleton apply fixture\n");
        File.WriteAllBytes(Path.Combine(directory, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(directory, "data.bin"), Enumerable.Range(0, 70_000)
            .Select(static i => (byte)(i * 31)).ToArray());
        Directory.CreateDirectory(Path.Combine(directory, "sub", "nested"));
        File.WriteAllText(Path.Combine(directory, "sub", "alpha.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(directory, "sub", "nested", "beta.txt"), "beta\n");
    }

    private (string Iso, string Skeleton, string Hash, string Source) CreateFixture()
    {
        string source = TempDirectory();
        Populate(source);
        string iso = TempFile(".iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string skeleton = TempFile(".skeleton.xiso");
        string hash = TempFile(".hash");
        Assert.True(XisoSkeleton.Petrify(iso, skeleton, hash, quiet: true));
        return (iso, skeleton, hash, source);
    }

    private static string Sha256(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    [Fact]
    public void Apply_PetrifyRoundTrip_ReproducesImage()
    {
        (string iso, string skeleton, string hash, string source) = CreateFixture();
        string output = TempFile(".iso");

        int files = XisoSkeleton.Apply(skeleton, source, output, hash);

        Assert.True(files >= 5);
        Assert.Equal(Sha256(iso), Sha256(output));
        Assert.Equal(new FileInfo(iso).Length, new FileInfo(output).Length);
    }

    [Fact]
    public void Apply_WithoutHashFile_AlsoReproducesImage()
    {
        (string iso, string skeleton, _, string source) = CreateFixture();
        string output = TempFile(".iso");

        Assert.True(XisoSkeleton.Apply(skeleton, source, output) >= 5);
        Assert.Equal(Sha256(iso), Sha256(output));
    }

    [Fact]
    public void Apply_MissingSourceFile_NamesFile()
    {
        (_, string skeleton, _, string source) = CreateFixture();
        File.Delete(Path.Combine(source, "sub", "alpha.txt"));
        string output = TempFile(".iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output));
        Assert.Contains("Missing source file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Apply_ExtraSourceFile_NamesFile()
    {
        (_, string skeleton, _, string source) = CreateFixture();
        File.WriteAllText(Path.Combine(source, "extra.txt"), "extra");
        string output = TempFile(".iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output));
        Assert.Contains("Extra source file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("extra.txt", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Apply_SizeMismatch_NamesFileAndSizes()
    {
        (_, string skeleton, _, string source) = CreateFixture();
        File.AppendAllText(Path.Combine(source, "readme.txt"), "more");
        string output = TempFile(".iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output));
        Assert.Contains("Size mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("readme.txt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_HashMismatch_NamesFile()
    {
        (_, string skeleton, string hash, string source) = CreateFixture();
        string[] lines = File.ReadAllLines(hash);
        int index = Array.FindIndex(lines, static line => line.Contains("readme.txt", StringComparison.Ordinal));
        Assert.True(index >= 0);
        lines[index] = new string('0', 40) + " /readme.txt";
        File.WriteAllLines(hash, lines);
        string output = TempFile(".iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output, hash));
        Assert.Contains("Hash mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("readme.txt", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Apply_ExtraHashEntry_NamesPath()
    {
        (_, string skeleton, string hash, string source) = CreateFixture();
        File.AppendAllText(hash, $"{new string('0', 40)} /ghost.bin{Environment.NewLine}");
        string output = TempFile(".iso");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => XisoSkeleton.Apply(skeleton, source, output, hash));
        Assert.Contains("Extra hash entry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ghost.bin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_OutputSameAsSkeleton_Throws()
    {
        (_, string skeleton, _, string source) = CreateFixture();

        Assert.Throws<IOException>(() => XisoSkeleton.Apply(skeleton, source, skeleton));
    }

    [Fact]
    public void Cli_ApplySkeleton_RestoresImage()
    {
        (string iso, string skeleton, string hash, string source) = CreateFixture();
        string output = TempFile(".iso");

        int exit = Program.Main([
            "apply-skeleton", skeleton, source, "-o", output, "--hash", hash, "-y",
        ]);

        Assert.Equal(0, exit);
        Assert.Equal(Sha256(iso), Sha256(output));
    }

    [Fact]
    public void Cli_ApplySkeleton_MissingFile_ReturnsOne()
    {
        (_, string skeleton, _, string source) = CreateFixture();
        File.Delete(Path.Combine(source, "readme.txt"));
        string output = TempFile(".iso");

        int exit = Program.Main(["apply-skeleton", skeleton, source, "-o", output, "-y"]);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(output));
    }
}
