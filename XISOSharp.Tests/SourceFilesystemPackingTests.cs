using System.Security.Cryptography;
using XISOSharp.Interfaces;

namespace XISOSharp.Tests;

/// <summary>
/// LIB-01: packing a caller-supplied source filesystem must produce the same
/// image as the host-directory path, and must remain CWD-free and independent
/// under concurrent creates.
/// </summary>
[Collection("Sequential")]
public sealed class SourceFilesystemPackingTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public void MemorySource_PacksByteIdenticalToHostDirectory()
    {
        string work = CreateTempDir();
        string hostRoot = Path.Combine(work, "src");
        Directory.CreateDirectory(Path.Combine(hostRoot, "sub", "nested"));
        Directory.CreateDirectory(Path.Combine(hostRoot, "sub", "empty"));
        Directory.CreateDirectory(Path.Combine(hostRoot, "empty"));
        File.WriteAllText(Path.Combine(hostRoot, "readme.txt"), "source filesystem\n");
        File.WriteAllBytes(Path.Combine(hostRoot, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(hostRoot, "sub", "data.bin"), Pattern(3000, 7));
        File.WriteAllText(Path.Combine(hostRoot, "sub", "nested", "deep.txt"), "deep\n");
        File.WriteAllText(Path.Combine(hostRoot, "skip.tmp"), "excluded\n");

        MemoryFilesystem memory = new();
        AddText(memory, "src/readme.txt", "source filesystem\n");
        AddBytes(memory, "src/empty.bin", []);
        AddBytes(memory, "src/sub/data.bin", Pattern(3000, 7));
        AddText(memory, "src/sub/nested/deep.txt", "deep\n");
        AddText(memory, "src/skip.tmp", "excluded\n");
        memory.CreateDirectory("src/sub/empty");
        memory.CreateDirectory("src/empty");

        string hostIso = Path.Combine(work, "host.iso");
        string memoryIso = Path.Combine(work, "memory.iso");
        string[] excludes = ["**/*.tmp"];

        Logger.Quiet = true;
        try
        {
            Assert.Equal(0, XisoWriter.PackFromDirectory(hostRoot, hostIso, excludes,
                fileTime: 0));
            Assert.Equal(0, XisoWriter.PackFromDirectory(memory, "src", memoryIso, excludes,
                fileTime: 0));
        }
        finally
        {
            Logger.Quiet = false;
        }

        Assert.Equal(Sha256(hostIso), Sha256(memoryIso));
        using XisoImage image = XisoImage.Open(memoryIso);
        List<string> paths = [.. image.ListFlat().Select(static e => e.FullPath)];
        Assert.Contains("/readme.txt", paths, StringComparer.Ordinal);
        Assert.Contains("/sub/nested/deep.txt", paths, StringComparer.Ordinal);
        Assert.Contains("/sub/empty", paths, StringComparer.Ordinal);
        Assert.DoesNotContain("/skip.tmp", paths, StringComparer.Ordinal);
    }

    [Fact]
    public void MemorySource_EmptyRoot_PacksAnEmptyImage()
    {
        string work = CreateTempDir();
        MemoryFilesystem memory = new();
        string iso = Path.Combine(work, "empty.iso");

        Logger.Quiet = true;
        try
        {
            Assert.Equal(0, XisoWriter.PackFromDirectory(memory, "", iso, fileTime: 0));
        }
        finally
        {
            Logger.Quiet = false;
        }

        Assert.True(File.Exists(iso));
        using XisoImage image = XisoImage.Open(iso);
        Assert.Empty(image.ListFlat());
    }

    [Fact]
    public void MemorySource_MissingRoot_ThrowsDirectoryNotFound()
    {
        MemoryFilesystem memory = new();
        string work = CreateTempDir();

        Assert.Throws<DirectoryNotFoundException>(() =>
            XisoWriter.PackFromDirectory(memory, "missing", Path.Combine(work, "x.iso")));
    }

    [Fact]
    public void MemorySource_Over4GbFile_ThrowsFileTooLarge()
    {
        string work = CreateTempDir();
        OversizedFilesystem source = new();

        Assert.Throws<XisoFileTooLargeException>(() =>
            XisoWriter.PackFromDirectory(source, "", Path.Combine(work, "large.iso")));
    }

    [Fact]
    public void SourceFilesystem_CaseInsensitiveDuplicate_Throws()
    {
        string work = CreateTempDir();

        Assert.Throws<InvalidDataException>(() =>
            XisoWriter.PackFromDirectory(new DuplicateNameFilesystem(), "", Path.Combine(work, "dup.iso")));
    }

    [Fact]
    public async Task ConcurrentMemorySources_AreIndependentAndCwdFree()
    {
        string work = CreateTempDir();
        string cwd = Directory.GetCurrentDirectory();

        MemoryFilesystem first = new();
        AddText(first, "one/a.txt", "first\n");
        AddBytes(first, "one/b.bin", Pattern(9000, 1));

        MemoryFilesystem second = new();
        AddText(second, "two/a.txt", "second\n");
        AddBytes(second, "two/b.bin", Pattern(9000, 2));

        Logger.Quiet = true;
        try
        {
            Task<int>[] jobs =
            [
                XisoWriter.PackFromDirectoryAsync(first, "one", Path.Combine(work, "one.iso"), fileTime: 0),
                XisoWriter.PackFromDirectoryAsync(second, "two", Path.Combine(work, "two.iso"), fileTime: 0),
            ];

            int[] results = await Task.WhenAll(jobs);
            Assert.All(results, static r => Assert.Equal(0, r));
        }
        finally
        {
            Logger.Quiet = false;
        }

        Assert.Equal(cwd, Directory.GetCurrentDirectory());
        Assert.Equal("first\n", ReadSingleFile(Path.Combine(work, "one.iso"), "/a.txt"));
        Assert.Equal("second\n", ReadSingleFile(Path.Combine(work, "two.iso"), "/a.txt"));
    }

    [Fact]
    public void MemoryFilesystem_EnumeratesDirectChildrenAndReadsSnapshots()
    {
        MemoryFilesystem memory = new();
        AddText(memory, "root.txt", "root");
        AddText(memory, "sub/a.txt", "a");
        memory.CreateDirectory("sub/empty");
        memory.CreateDirectory("other");

        IReadOnlyList<SourceEntry> root = memory.EnumerateEntries("");
        Assert.Contains(root, static e => e is { Name: "root.txt", IsDirectory: false, Length: 4 });
        Assert.Contains(root, static e => e is { Name: "sub", IsDirectory: true });
        Assert.Contains(root, static e => e is { Name: "other", IsDirectory: true });

        IReadOnlyList<SourceEntry> sub = memory.EnumerateEntries("sub");
        Assert.Contains(sub, static e => e is { Name: "a.txt", IsDirectory: false, Length: 1 });
        Assert.Contains(sub, static e => e is { Name: "empty", IsDirectory: true });

        using Stream stream = memory.OpenRead("root.txt");
        using StreamReader reader = new(stream);
        Assert.Equal("root", reader.ReadToEnd());
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_sourcefs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void AddText(MemoryFilesystem filesystem, string path, string text)
    {
        using Stream stream = filesystem.CreateFile(path);
        using StreamWriter writer = new(stream);
        writer.Write(text);
    }

    private static void AddBytes(MemoryFilesystem filesystem, string path, byte[] bytes)
    {
        using Stream stream = filesystem.CreateFile(path);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReadSingleFile(string iso, string path)
    {
        using XisoImage image = XisoImage.Open(iso);
        byte[] bytes = image.ReadFile(path);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Store that can represent names differing only by case (unlike a host directory on Windows).</summary>
    private sealed class DuplicateNameFilesystem : ISourceFilesystem
    {
        public bool DirectoryExists(string path) => path.Length == 0;

        public IReadOnlyList<SourceEntry> EnumerateEntries(string path) =>
            path.Length == 0
                ? [
                    new SourceEntry("a.txt", IsDirectory: false, Length: 1),
                    new SourceEntry("A.TXT", IsDirectory: false, Length: 1)
                ]
                : [];

        public Stream OpenRead(string path) => new MemoryStream([1]);
    }

    /// <summary>Source store that reports a file larger than XISO can represent.</summary>
    private sealed class OversizedFilesystem : ISourceFilesystem
    {
        public bool DirectoryExists(string path) => path.Length == 0;

        public IReadOnlyList<SourceEntry> EnumerateEntries(string path) =>
            path.Length == 0
                ? [new SourceEntry("huge.bin", IsDirectory: false, Length: (long)uint.MaxValue + 1)]
                : [];

        public Stream OpenRead(string path) => new MemoryStream([1]);
    }
}
