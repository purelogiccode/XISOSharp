using System.Security.Cryptography;

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
            catch
            {
                /* best effort */
            }

            try
            {
                if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                /* best effort */
            }
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
        var rc = XisoWriter.CreateXiso(sourceDir, outDir, null, null, out var outPath, null, null);
        Assert.Equal(0, rc);
        Assert.NotNull(outPath);
        Assert.True(File.Exists(outPath));
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
        return outPath;
    }

    private static byte[] ComputeSha256(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static byte[] ComputeSha256Bytes(byte[] data) => SHA256.HashData(data);

    [Fact]
    public void CompressToCso_FromIsoFile_ProducesCsoAndIsCsoTrue()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "out.cso");

        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
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

        var rc = CisoWriter.CompressToCso(SourceDir, csoPath, level: 6);
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
        var cRc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, cRc);

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "rt.iso");
        var dRc = CisoReader.DecompressToIso(csoPath, decPath);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 0);
        Assert.Equal(0, rc);
        Assert.True(CisoReader.IsCso(csoPath));

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "level0.iso");
        var dRc = CisoReader.DecompressToIso(csoPath, decPath);
        Assert.Equal(0, dRc);
        Assert.Equal(origHash, ComputeSha256(decPath));
    }

    [Fact]
    public void CompressToCso_WithVariousLevels_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        foreach (var level in new[] { 1, 6, 9 })
        {
            var csoDir = CreateTempDir();
            var csoPath = Path.Combine(csoDir, $"level{level}.cso");
            var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: level);
            Assert.Equal(0, rc);
            Assert.True(CisoReader.IsCso(csoPath));

            var decDir = CreateTempDir();
            var decPath = Path.Combine(decDir, $"level{level}.iso");
            var dRc = CisoReader.DecompressToIso(csoPath, decPath);
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

        var cRc = await CisoWriter.CompressToCsoAsync(isoPath, csoPath, level: 6);
        Assert.Equal(0, cRc);
        Assert.True(CisoReader.IsCso(csoPath));

        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "async.iso");
        var dRc = await CisoReader.DecompressToIsoAsync(csoPath, decPath);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        // Read 3000 bytes starting at 1000 (crosses sector boundary at 2048)
        const long offset = 1000;
        var len = 3000;
        // Ensure we don't go past EOF
        if (offset + len > isoBytes.Length) len = isoBytes.Length - (int)offset;
        Assert.True(len > 2048);

        var buf = new byte[len];
        CisoReader.ReadFromCso(csoPath, offset, buf.AsSpan());
        Assert.True(buf.AsSpan().SequenceEqual(isoBytes.AsSpan((int)offset, len)));

        // Same via FileStream overload at different offset
        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        const long offset2 = 2048;
        const int len2 = 2048;
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
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
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var bytes = File.ReadAllBytes(csoPath);
        // Parse header to get index length, corrupt index entry 1 to be before 0
        // Header: magic 4, headerSize 4, uncompressedSize 8, blockSize 4, version 1, align 1, unused 2
        if (bytes.Length > 32)
        {
            // Read total blocks to find index offset
            var uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            var blockSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
            var totalBlocks = (long)((uncompressedSize + blockSize - 1) / blockSize);
            var indexLen = totalBlocks + 1;
            // index starts at 24
            // Make entry 1 < entry 0 by swapping bytes
            if (indexLen >= 2 && bytes.Length >= 24 + 8)
            {
                // Set second entry to 0x00000000 (plain bit clear, offset 0) while first is plain with high offset -> next < offset will trigger
                // Simpler: copy first entry's value minus 1 into second entry
                var first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
                var second = first - 1;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), second);
            }
        }

        var corruptPath = Path.Combine(csoDir, "corrupt2.cso");
        File.WriteAllBytes(corruptPath, bytes);

        using var src = new FileStream(corruptPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var dst = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CisoReader.DecompressStream(src, dst));
    }

    // ---- LZ4 codec (lz4_flex byte-exact port) ----

    private static byte[] Filled(byte value, int len)
    {
        var data = new byte[len];
        data.AsSpan().Fill(value);
        return data;
    }

    private static string CompressToHex(byte[] input)
    {
        var dst = new byte[Lz4.MaxCompressedOutputSize(input.Length)];
        var n = Lz4.Compress(input, dst);
        Assert.True(n > 0);
        return Convert.ToHexString(dst.AsSpan(0, n));
    }

    [Fact]
    public void Lz4_Compress_MatchesLz4Flex_GoldenVectors()
    {
        // Hand-traced lz4_flex 0.11.3 outputs (the compressor behind ciso 0.2 / xdvdfs compress):
        // 13 x 'a': seed at 0, match at offset 1 (total length 4+2, clipped by END_OFFSET=6), 6 last literals.
        Assert.Equal("1261010060616161616161", CompressToHex(Filled(0x61, 13)));

        // 12 bytes < LZ4_MIN_LENGTH (13): literals only, token 0xC0.
        Assert.Equal("C0616161616161616161616161", CompressToHex(Filled(0x61, 12)));

        // 2048 zero bytes: match at offset 1, duplicate length 2037 (>= 0xF -> token 0x1F),
        // extended integer 2037-15=2022 = 7x0xFF + 0xED, 6 trailing literals.
        Assert.Equal("1F000100FFFFFFFFFFFFFFED60000000000000", CompressToHex(Filled(0x00, 2048)));
    }

    [Fact]
    public void Lz4_Compress_Decompress_RoundTrips_RandomAndStructuredData()
    {
        var rng = new Random(1234);

        for (var t = 0; t < 100; t++)
        {
            var len = rng.Next(1, 4096);
            var data = new byte[len];
            rng.NextBytes(data);
            RoundTripLz4(data);
        }

        // Low-entropy runs exercise match extension and overlapping copies.
        for (var t = 0; t < 100; t++)
        {
            var len = rng.Next(1, 4096);
            var data = new byte[len];
            var pos = 0;
            while (pos < len)
            {
                var run = Math.Min(rng.Next(1, 40), len - pos);
                data.AsSpan(pos, run).Fill((byte)rng.Next(0, 4));
                pos += run;
            }

            RoundTripLz4(data);
        }
    }

    private static void RoundTripLz4(byte[] data)
    {
        var dst = new byte[Lz4.MaxCompressedOutputSize(data.Length)];
        var n = Lz4.Compress(data, dst);
        var back = new byte[data.Length];
        var m = Lz4.Decompress(dst.AsSpan(0, n), back);
        Assert.True(m == data.Length, $"decompressed {m} != {data.Length}");
        Assert.True(back.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void Lz4_Decompress_RejectsMalformedBlocks()
    {
        // Zero match offset is invalid.
        var bad = new byte[] { 0x10, 0x41, 0x00, 0x00 }; // literal 'A', offset 0
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(bad, new byte[64]));

        // Literal length overruns the source.
        var truncated = new byte[] { 0xF0, 0xFF, 0x00 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(truncated, new byte[64]));

        // Match overruns the destination.
        var overrun = new byte[] { 0x1F, 0x41, 0x01, 0x00, 0xFF, 0xFF, 0x00 }; // lit 'A', offset 1, match len 529
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(overrun, new byte[8]));
    }

    // ---- CISO v2 (LZ4) writer ----

    private static (byte version, byte align, uint[] index) ParseCsoHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> header = stackalloc byte[24];
        fs.ReadExactly(header);
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        Assert.Equal(CisoReader.Magic, magic);
        var uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
        var totalBlocks = (long)((uncompressedSize + CisoWriter.BlockSize - 1) / CisoWriter.BlockSize);
        var index = new uint[totalBlocks + 1];
        Span<byte> le = stackalloc byte[4];
        for (var i = 0; i < index.Length; i++)
        {
            fs.ReadExactly(le);
            index[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(le);
        }

        return (header[20], header[21], index);
    }

    [Fact]
    public void CompressToCso_DefaultVersion_WritesV2HeaderWithAlign2()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "v2.cso");

        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 9);
        Assert.Equal(0, rc);
        Assert.True(CisoReader.IsCso(csoPath));

        var (version, align, _) = ParseCsoHeader(csoPath);
        Assert.Equal(CisoWriter.VersionLz4, version);
        Assert.Equal(2, align); // ciso 0.2 fixed alignment
    }

    [Fact]
    public void CompressToCso_Version2_RoundTrip_PreservesSha256()
    {
        var isoPath = CreateTempIso();
        var origHash = ComputeSha256(isoPath);

        foreach (var level in new[] { 0, 1, 9 })
        {
            var csoDir = CreateTempDir();
            var csoPath = Path.Combine(csoDir, $"v2l{level}.cso");
            var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: level, version: CisoWriter.VersionLz4);
            Assert.Equal(0, rc);
            Assert.True(CisoReader.IsCso(csoPath));

            var decDir = CreateTempDir();
            var decPath = Path.Combine(decDir, $"v2l{level}.iso");
            Assert.Equal(0, CisoReader.DecompressToIso(csoPath, decPath));
            Assert.Equal(origHash, ComputeSha256(decPath));
        }
    }

    [Fact]
    public void CompressToCso_Version2_Level0_AllSectorsPlain()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "v2store.cso");

        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 0, version: CisoWriter.VersionLz4);
        Assert.Equal(0, rc);

        var (_, _, index) = ParseCsoHeader(csoPath);
        // v2: high bit set = compressed; level 0 must flag every sector plain.
        for (var i = 0; i < index.Length - 1; i++)
            Assert.Equal(0u, index[i] & 0x80000000u);
    }

    [Fact]
    public void CompressToCso_Version2_Level9_CompressesSectors()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "v2best.cso");

        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 9, version: CisoWriter.VersionLz4);
        Assert.Equal(0, rc);

        var (_, _, index) = ParseCsoHeader(csoPath);
        // The XISO has large 0xFF gap regions; at least one sector must store compressed.
        var compressed = index.Take(index.Length - 1).Count(e => (e & 0x80000000u) != 0);
        Assert.True(compressed > 0, "expected at least one compressed sector");
    }

    [Fact]
    public void CompressStream_Version2_DecompressStream_RoundTrip()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        using var src = new MemoryStream(isoBytes, writable: false);
        using var compressed = new MemoryStream();
        CisoWriter.CompressStream(src, compressed, level: 9, version: CisoWriter.VersionLz4);

        compressed.Seek(0, SeekOrigin.Begin);
        using var decompressed = new MemoryStream();
        CisoReader.DecompressStream(compressed, decompressed);

        Assert.Equal(isoBytes.Length, decompressed.Length);
        Assert.Equal(ComputeSha256Bytes(isoBytes), ComputeSha256Bytes(decompressed.ToArray()));
    }

    [Fact]
    public void DecompressStream_HandCraftedV2RawBlockFrame()
    {
        // Craft a v2 CSO whose single sector is stored as an in-frame uncompressed block:
        // payload = [u32 LE 0x80000800 (raw block, 2048 bytes)][2048 raw bytes].
        var pattern = new byte[2048];
        for (var i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i * 7);

        var dataStart = 24 + 4 * 2; // header + 2 index entries
        var cso = new MemoryStream();
        var header = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), CisoReader.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), CisoReader.HeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), 2048ul);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 2048u);
        header[20] = CisoWriter.VersionLz4;
        header[21] = 2;
        cso.Write(header);

        // Index: compressed-flagged entry pointing at the payload, final entry after it.
        var index = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            index.AsSpan(0, 4), (uint)(dataStart >> 2) | 0x80000000u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            index.AsSpan(4, 4), (uint)((dataStart + 4 + 2048) >> 2));
        cso.Write(index);

        var payload = new byte[4 + 2048];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0x80000000u | 2048u);
        pattern.CopyTo(payload.AsSpan(4));
        cso.Write(payload);

        cso.Seek(0, SeekOrigin.Begin);
        using var dest = new MemoryStream();
        CisoReader.DecompressStream(cso, dest);

        Assert.Equal(pattern, dest.ToArray());
    }

    [Fact]
    public void CompressToCso_InvalidVersion_ThrowsArgumentOutOfRange()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "bad.cso");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CisoWriter.CompressToCso(isoPath, csoPath, level: 6, version: 3));
    }

    // ---- Split CSO output / input (ciso::split parity) ----

    private (string isoPath, string csoPath, string csoDir) CreateSplitCso(string? outputName = null,
        long splitBytes = 16384, int level = 9, byte version = CisoWriter.VersionLz4)
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, outputName ?? "split.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: level, splitBytes: splitBytes, version: version);
        Assert.Equal(0, rc);
        return (isoPath, csoPath, csoDir);
    }

    private static List<string> SplitParts(string csoPath)
    {
        var parts = new List<string>();
        var baseName = csoPath[..^4]; // strip ".cso"
        for (var i = 1; ; i++)
        {
            var part = $"{baseName}.{i}.cso";
            if (!File.Exists(part)) break;
            parts.Add(part);
        }

        return parts;
    }

    [Fact]
    public void CompressToCso_WithSplit_WritesNumberedPartsAndRoundTrips()
    {
        var (isoPath, csoPath, _) = CreateSplitCso(splitBytes: 16384);
        var parts = SplitParts(csoPath);
        Assert.True(parts.Count >= 2, $"expected at least 2 parts, got {parts.Count}");

        // Part names are <base>.1.cso, <base>.2.cso, …
        Assert.EndsWith(".1.cso", parts[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".2.cso", parts[1], StringComparison.OrdinalIgnoreCase);
        Assert.True(CisoReader.IsCso(parts[0]));

        var origHash = ComputeSha256(isoPath);
        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "split.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(parts[0], decPath));
        Assert.Equal(origHash, ComputeSha256(decPath));
    }

    [Fact]
    public void CompressToCso_WithSplit_LargerThanImage_ProducesSinglePart()
    {
        var (_, csoPath, _) = CreateSplitCso(splitBytes: 1 << 30);
        var parts = SplitParts(csoPath);
        var singlePart = Assert.Single(parts);
        Assert.EndsWith(".1.cso", singlePart, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompressToCso_WithSplit_OutputWithoutExtension_StillNumbered()
    {
        var (_, _, csoDir) = CreateSplitCso(outputName: "noext", splitBytes: 16384);
        Assert.True(File.Exists(Path.Combine(csoDir, "noext.1.cso")));
    }

    [Fact]
    public void ReadFromCso_SplitInput_RandomAccessAcrossParts()
    {
        var (isoPath, csoPath, _) = CreateSplitCso(splitBytes: 8192);
        var isoBytes = File.ReadAllBytes(isoPath);
        var parts = SplitParts(csoPath);
        Assert.True(parts.Count >= 2);

        // Read the first 512 bytes and a window crossing the split boundary.
        Span<byte> buf = stackalloc byte[512];
        CisoReader.ReadFromCso(parts[0], 0, buf);
        Assert.True(buf.SequenceEqual(isoBytes.AsSpan(0, 512)));

        var boundary = new FileInfo(parts[0]).Length;
        if (boundary + 512 < isoBytes.Length)
        {
            var cross = new byte[1024];
            CisoReader.ReadFromCso(parts[0], boundary - 512, cross);
            Assert.True(cross.AsSpan().SequenceEqual(isoBytes.AsSpan((int)boundary - 512, 1024)));
        }
    }

    [Fact]
    public void CisoBlockDevice_SplitInput_MatchesOriginalIso()
    {
        var (isoPath, csoPath, _) = CreateSplitCso(splitBytes: 8192);
        var isoBytes = File.ReadAllBytes(isoPath);
        var parts = SplitParts(csoPath);
        Assert.True(parts.Count >= 2);

        using var dev = new XISOSharp.BlockDevice.CisoBlockDevice(parts[0]);
        Assert.Equal(isoBytes.Length, dev.Length);

        var buf = new byte[4096];
        for (var offset = 0; offset < isoBytes.Length; offset += buf.Length)
        {
            var n = dev.Read(offset, buf);
            var expected = Math.Min(buf.Length, isoBytes.Length - offset);
            Assert.Equal(expected, n);
            Assert.True(buf.AsSpan(0, expected).SequenceEqual(isoBytes.AsSpan(offset, expected)),
                $"block device mismatch at offset {offset}");
        }
    }

    [Fact]
    public void CompressToCso_SplitVersion1_RoundTrips()
    {
        var (isoPath, csoPath, _) = CreateSplitCso(splitBytes: 16384, level: 6, version: CisoWriter.VersionDeflate);
        var parts = SplitParts(csoPath);
        Assert.True(parts.Count >= 2);

        var (version, _, _) = ParseCsoHeader(parts[0]);
        Assert.Equal(CisoWriter.VersionDeflate, version);

        var origHash = ComputeSha256(isoPath);
        var decDir = CreateTempDir();
        var decPath = Path.Combine(decDir, "splitv1.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(parts[0], decPath));
        Assert.Equal(origHash, ComputeSha256(decPath));
    }
}