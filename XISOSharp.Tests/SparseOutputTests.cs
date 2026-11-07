namespace XISOSharp.Tests;

/// <summary>
/// LIB-03: pre-sized outputs are marked sparse where the file system supports
/// it, and the existing extraction/compression paths still round-trip.
/// </summary>
[Collection("Sequential")]
public sealed class SparseOutputTests : IDisposable
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
                // Best effort.
            }
        }
    }

    [RequiresNtfsFact]
    public void Prepare_PreSizesWithoutAllocatingTheWholeRange()
    {
        string path = Path.Combine(CreateTempDir(), "large.bin");
        const long length = 128L * 1024 * 1024;

        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            SparseFile.Prepare(stream, length);
            Assert.Equal(length, stream.Length);
            stream.WriteByte(0x42);

            long allocated = SparseFile.GetAllocatedSize(stream);
            Assert.True(allocated >= 0, "Windows should report the allocation size.");
            Assert.True(allocated < length / 4,
                $"Expected a sparse allocation below {length / 4} bytes, got {allocated}.");
        }

        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(0x42, File.ReadAllBytes(path)[0]);
    }

    [RequiresNtfsFact]
    public void LocalFilesystem_ExpectedLength_IsSparse()
    {
        string root = CreateTempDir();
        LocalFilesystem filesystem = new(root);
        const long length = 64L * 1024 * 1024;

        using (Stream stream = filesystem.CreateFile("file.bin", length))
        {
            Assert.Equal(length, stream.Length);
            stream.WriteByte(1);
            if (stream is FileStream fileStream)
            {
                long allocated = SparseFile.GetAllocatedSize(fileStream);
                Assert.True(allocated >= 0);
                Assert.True(allocated < length / 4,
                    $"Expected a sparse allocation below {length / 4} bytes, got {allocated}.");
            }
        }
    }

    [Fact]
    public void CisoDecompress_ZeroHeavyImage_RoundTrips()
    {
        string work = CreateTempDir();
        string source = Path.Combine(work, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "zeros\n");
        byte[] zeros = new byte[512 * 1024];
        File.WriteAllBytes(Path.Combine(source, "zeros.bin"), zeros);

        string iso = Path.Combine(work, "image.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(source, iso, fileTime: 0));

        string cso = Path.Combine(work, "image.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(iso, cso, level: 1));

        string extracted = Path.Combine(work, "extracted.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(cso, extracted));

        Assert.Equal(Sha256(iso), Sha256(extracted));
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"xiso_sparse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
