using XISOSharp.Interfaces;

namespace XISOSharp.Tests;

/// <summary>
/// PlanToEnhance Phase 7: known-length destinations are pre-sized before the
/// copy loop, and custom <see cref="IFilesystem"/> stores receive the expected
/// length through the new overload (without breaking the old one).
/// </summary>
[Collection("Sequential")]
public sealed class PreallocationTests : IDisposable
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
    public void LocalFilesystem_WithExpectedLength_PreSizesTheFile()
    {
        string root = CreateTempDir();
        LocalFilesystem filesystem = new(root);

        using (Stream stream = filesystem.CreateFile("large.bin", 123456))
        {
            Assert.Equal(123456, stream.Length);
            stream.WriteByte(0x42);
        }

        Assert.Equal(123456, new FileInfo(Path.Combine(root, "large.bin")).Length);
    }

    [Fact]
    public void LocalFilesystem_WithoutExpectedLength_StartsEmpty()
    {
        string root = CreateTempDir();
        LocalFilesystem filesystem = new(root);

        using (Stream stream = filesystem.CreateFile("small.bin"))
        {
            Assert.Equal(0, stream.Length);
            stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
        }

        Assert.Equal(3, new FileInfo(Path.Combine(root, "small.bin")).Length);
    }

    [Fact]
    public void MemoryFilesystem_WithExpectedLength_PublishesOnlyWrittenBytes()
    {
        MemoryFilesystem filesystem = new();

        using (Stream stream = filesystem.CreateFile("data.bin", 4096))
        {
            stream.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        }

        Assert.Equal(4, filesystem.FileLength("data.bin"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, filesystem.ReadAllBytes("data.bin"));
    }

    [Fact]
    public void CustomFilesystem_DefaultOverload_FallsBackToCreateFile()
    {
        MinimalFilesystem concrete = new();
        IFilesystem filesystem = concrete;

        using Stream stream = filesystem.CreateFile("a.bin", 99);

        Assert.Equal(0, stream.Length);
        Assert.Equal(1, concrete.CreateCalls);
    }

    [Fact]
    public void UnpackImage_PassesEachEntrySizeToTheFilesystem()
    {
        string work = CreateTempDir();
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "b.bin"), new string('x', 5000));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "c.txt"), "nested");

        string isoDir = Path.Combine(work, "iso");
        Directory.CreateDirectory(isoDir);
        Assert.Equal(0, XisoWriter.CreateXiso(src, isoDir, null, null, out string? isoPath, "game.iso", null));
        Assert.NotNull(isoPath);

        RecordingFilesystem destination = new();
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, destination));

        Assert.Equal(3, destination.ExpectedLengths.Count);
        Assert.Contains(5L, destination.ExpectedLengths.Values);
        Assert.Contains(5000L, destination.ExpectedLengths.Values);
        Assert.Contains(6L, destination.ExpectedLengths.Values);
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_prealloc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Store that only implements the original member.</summary>
    private sealed class MinimalFilesystem : IFilesystem
    {
        public int CreateCalls { get; private set; }

        public Stream CreateFile(string path)
        {
            CreateCalls++;
            return new MemoryStream();
        }

        public void CreateDirectory(string path)
        {
        }

        public bool FileExists(string path) => false;

        public long FileLength(string path) => -1;
    }

    /// <summary>Records the expected lengths supplied by extraction.</summary>
    private sealed class RecordingFilesystem : IFilesystem
    {
        private readonly MemoryFilesystem _inner = new();

        public Dictionary<string, long?> ExpectedLengths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Stream CreateFile(string path) => _inner.CreateFile(path);

        public Stream CreateFile(string path, long? expectedLength)
        {
            ExpectedLengths[path] = expectedLength;
            return _inner.CreateFile(path, expectedLength);
        }

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool FileExists(string path) => _inner.FileExists(path);

        public long FileLength(string path) => _inner.FileLength(path);

        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
