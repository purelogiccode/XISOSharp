using System.Security.Cryptography;
using XISOSharp.BlockDevice;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for CisoWriter / CisoReader: compression, decompression, stream variants,
/// IsCso detection, random-access reads and error handling.
/// Mirrors IntegrationTests style with temp-directory isolation.
/// </summary>
[Collection("Sequential")]
public class CisoTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* best effort */ }

            try
            {
                if (File.Exists(dir)) File.Delete(dir);
            }
            catch { /* best effort */ }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xiso_ciso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTempIso(string? sourceDir = null)
    {
        sourceDir ??= SourceDir;
        var outDir = CreateTempDir();
        int rc = XisoWriter.CreateXiso(sourceDir, outDir, null, null, out var outPath, null, null);
        Assert.Equal(0, rc);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
        return outPath!;
    }

    private static byte[] ComputeSha256(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static byte[] ComputeSha256Bytes(byte[] data) => SHA256.HashData(data);

    [Fact]
    public void CompressToCso_FromIsoFile_ProducesCsoAndIsCsoTrue()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "out.cso");

        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);
        Assert.True(File.Exists(csoPath));
        Assert.True(CisoReader.IsCso(csoPath));
        Assert.True(new FileInfo(csoPath).Length > 0);
        Assert.True(new FileInfo(csoPath).Length >= 24); // at least header
    }

    [Fact]
    public void CompressToCso_FromDirectory_DirectlyProducesCso()
    {
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "dir.cso");

        int rc = CisoWriter.CompressToCso(SourceDir, csoPath, level: 6);
        Assert.Equal(0, rc);
        Assert.True(File.Exists(csoPath));
        Assert.True(CisoReader.IsCso(csoPath));
    }

    [Fact]
    public void DecompressToIso_RoundTrip_PreservesContentSha256()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "rt.cso");
        int cRc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, cRc);

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "rt.iso");
        int dRc = CisoReader.DecompressToIso(csoPath, decPath);
        Assert.Equal(0, dRc);
        Assert.True(File.Exists(decPath));

        var decHash = ComputeSha256(decPath);
        Assert.Equal(origHash, decHash);
    }

    [Fact]
    public void CompressStream_DecompressStream_MemoryStreams_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        using var src = new MemoryStream(isoBytes, writable: false);
        using var compressed = new MemoryStream();
        CisoWriter.CompressStream(src, compressed, level: 6);

        Assert.True(compressed.Length > 24);

        compressed.Seek(0, SeekOrigin.Begin);
        using var decompressed = new MemoryStream();
        CisoReader.DecompressStream(compressed, decompressed);

        var result = decompressed.ToArray();
        // decompressed length should equal original (may include padding trimmed to original size)
        Assert.Equal(isoBytes.Length, result.Length);
        Assert.Equal(ComputeSha256Bytes(isoBytes), ComputeSha256Bytes(result));
    }

    [Fact]
    public void CompressToCso_WithLevelZero_StoresPlainAndRoundTrips()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "level0.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 0);
        Assert.Equal(0, rc);
        Assert.True(CisoReader.IsCso(csoPath));

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "level0.iso");
        int dRc = CisoReader.DecompressToIso(csoPath, decPath);
        Assert.Equal(0, dRc);
        Assert.Equal(origHash, ComputeSha256(decPath));
    }

    [Fact]
    public void CompressToCso_WithVariousLevels_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        foreach (int level in new[] { 1, 6, 9 })
        {
            var csoDir = CreateTempDir();
            var csoPath = Path.Combine(csoDir, $"level{level}.cso");
            int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: level);
            Assert.Equal(0, rc);
            Assert.True(CisoReader.IsCso(csoPath));

            var decDir = CreateTempDir();
            var decPath = Path.Combine(decDir, $"level{level}.iso");
            int dRc = CisoReader.DecompressToIso(csoPath, decPath);
            Assert.Equal(0, dRc);
            Assert.Equal(origHash, ComputeSha256(decPath));
        }
    }

    [Fact]
    public async Task CompressToCsoAsync_And_DecompressToIsoAsync_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "async.cso");

        int cRc = await CisoWriter.CompressToCsoAsync(isoPath, csoPath, level: 6);
        Assert.Equal(0, cRc);
        Assert.True(CisoReader.IsCso(csoPath));

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "async.iso");
        int dRc = await CisoReader.DecompressToIsoAsync(csoPath, decPath);
        Assert.Equal(0, dRc);
        Assert.Equal(origHash, ComputeSha256(decPath));
    }

    [Fact]
    public void CompressStream_And_DecompressStream_ViaFileStreams_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "stream.cso");
        using (var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        using (var dst = new FileStream(csoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            CisoWriter.CompressStream(src, dst, level: 6);
        }
        Assert.True(CisoReader.IsCso(csoPath));

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "stream.iso");
        using (var src = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
        using (var dst = new FileStream(decPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            CisoReader.DecompressStream(src, dst);
        }
        Assert.Equal(origHash, ComputeSha256(decPath));
    }

    [Fact]
    public void ReadFromCso_StringOverload_ReadsHeaderAtZero()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "read.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        Span<byte> buf = stackalloc byte[512];
        CisoReader.ReadFromCso(csoPath, 0, buf);
        Assert.True(buf.SequenceEqual(isoBytes.AsSpan(0, 512)));
    }

    [Fact]
    public void ReadFromCso_FileStreamOverload_ReadsSectorZero()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "read2.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> buf = stackalloc byte[512];
        CisoReader.ReadFromCso(fs, 0, buf);
        Assert.True(buf.SequenceEqual(isoBytes.AsSpan(0, 512)));
    }

    [Fact]
    public void ReadFromCso_RandomAccess_CrossSectorAndMidFile()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "read3.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        // Read 3000 bytes starting at 1000 (crosses sector boundary at 2048)
        long offset = 1000;
        int len = 3000;
        // Ensure we don't go past EOF
        if (offset + len > isoBytes.Length) len = isoBytes.Length - (int)offset;
        Assert.True(len > 2048);

        var buf = new byte[len];
        CisoReader.ReadFromCso(csoPath, offset, buf.AsSpan());
        Assert.True(buf.AsSpan().SequenceEqual(isoBytes.AsSpan((int)offset, len)));

        // Same via FileStream overload at different offset
        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        long offset2 = 2048;
        int len2 = 2048;
        if (offset2 + len2 <= isoBytes.Length)
        {
            var buf2 = new byte[len2];
            CisoReader.ReadFromCso(fs, offset2, buf2.AsSpan());
            Assert.True(buf2.AsSpan().SequenceEqual(isoBytes.AsSpan((int)offset2, len2)));
        }
    }

    [Fact]
    public void IsCso_ReturnsFalse_ForPlainIso()
    {
        var isoPath = CreateTempIso();
        Assert.False(CisoReader.IsCso(isoPath));
    }

    [Fact]
    public void IsCso_ReturnsFalse_ForTruncatedFile()
    {
        var tmpDir = CreateTempDir();
        var truncated = Path.Combine(tmpDir, "trunc.cso");
        File.WriteAllBytes(truncated, new byte[10]); // less than header size
        Assert.False(CisoReader.IsCso(truncated));
        Assert.False(CisoReader.IsCso(Path.Combine(tmpDir, "nonexistent.cso")));
    }

    [Fact]
    public void CompressToCso_MissingSource_ThrowsFileNotFoundException()
    {
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "out.cso");
        var missing = Path.Combine(csoDir, "does_not_exist.iso");
        Assert.Throws<FileNotFoundException>(() => CisoWriter.CompressToCso(missing, csoPath));
    }

    [Fact]
    public void CompressToCso_SameSourceAndDest_ThrowsIOException()
    {
        var isoPath = CreateTempIso();
        Assert.Throws<IOException>(() => CisoWriter.CompressToCso(isoPath, isoPath));
    }

    [Fact]
    public void DecompressToIso_MissingCso_ThrowsFileNotFoundException()
    {
        var tmpDir = CreateTempDir();
        var missing = Path.Combine(tmpDir, "missing.cso");
        var outIso = Path.Combine(tmpDir, "out.iso");
        Assert.Throws<FileNotFoundException>(() => CisoReader.DecompressToIso(missing, outIso));
    }

    [Fact]
    public void DecompressStream_TruncatedCso_ThrowsInvalidDataException()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "trunc2.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var bytes = File.ReadAllBytes(csoPath);
        // Truncate to header + partial index (corrupt)
        var truncated = bytes[..((int)CisoWriter.HeaderSize + 4)]; // header + one index entry, not full

        using var src = new MemoryStream(truncated, writable: false);
        using var dst = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CisoReader.DecompressStream(src, dst));
    }

    [Fact]
    public void DecompressStream_InvalidMagic_ThrowsInvalidDataException()
    {
        var bad = new MemoryStream();
        // Write header with bad magic
        bad.Write(new byte[24]); // all zeros -> magic 0
        // Also need index? DecompressStream will check magic first so index not needed
        bad.Seek(0, SeekOrigin.Begin);
        using var dst = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CisoReader.DecompressStream(bad, dst));
    }

    [Fact]
    public void ReadFromCso_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "oor.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var isoLen = new FileInfo(isoPath).Length;
        var buf = new byte[100];
        // offset beyond uncompressed size
        Assert.Throws<ArgumentOutOfRangeException>(() => CisoReader.ReadFromCso(csoPath, isoLen + 100, buf.AsSpan()));
    }

    [Fact]
    public void DecompressStream_InvalidIndexCorruption_ThrowsInvalidDataException()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "corrupt.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var bytes = File.ReadAllBytes(csoPath);
        // Parse header to get index length, corrupt index entry 1 to be before 0
        // Header: magic 4, headerSize 4, uncompressedSize 8, blockSize 4, version 1, align 1, unused 2
        if (bytes.Length > 32)
        {
            // Read total blocks to find index offset
            ulong uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            uint blockSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            long totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
            long indexLen = totalBlocks + 1;
            // index starts at 24
            // Make entry 1 < entry 0 by swapping bytes
            if (indexLen >= 2 && bytes.Length >= 24 + 8)
            {
                // Set second entry to 0x00000000 (plain bit clear, offset 0) while first is plain with high offset -> next < offset will trigger
                // Simpler: copy first entry's value minus 1 into second entry
                uint first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
                uint second = first - 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), second);
            }
        }
        var corruptPath = Path.Combine(csoDir, "corrupt2.cso");
        File.WriteAllBytes(corruptPath, bytes);

        using var src = new FileStream(corruptPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var dst = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CisoReader.DecompressStream(src, dst));
    }
}


