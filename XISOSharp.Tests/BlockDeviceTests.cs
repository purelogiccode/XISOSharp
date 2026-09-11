using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;
using XISOSharp.Interfaces;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for IBlockDevice implementations: MemoryBlockDevice, FileBlockDevice,
/// OffsetBlockDevice and CisoBlockDevice.
/// </summary>
[Collection("Sequential")]
public class BlockDeviceTests : IDisposable
{
    // Resolved via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal.
    private static readonly string TestDataRoot = TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory);

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];
    private readonly List<IBlockDevice> _devices = [];

    public void Dispose()
    {
        foreach (IBlockDevice d in _devices)
        {
            try
            {
                d.Dispose();
            }
            catch
            {
                /* ignore */
            }
        }

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

        foreach (string f in _tempFiles)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch
            {
                // ignored
            }
        }
    }

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xiso_ciso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateTempIso(string? sourceDir = null)
    {
        sourceDir ??= SourceDir;
        string outDir = CreateTempDir();
        int rc = XisoWriter.CreateXiso(sourceDir, outDir, null, null, out string? outPath, null, null);
        Assert.Equal(0, rc);
        Assert.NotNull(outPath);
        return outPath;
    }

    // ---- MemoryBlockDevice ----

    [Fact]
    public void MemoryBlockDevice_WriteAndRead_Basic()
    {
        MemoryBlockDevice dev = new();
        _devices.Add(dev);

        byte[] data = "hello world"u8.ToArray();
        dev.Write(0, data);

        Assert.Equal(data.Length, dev.Length);

        Span<byte> buf = stackalloc byte[data.Length];
        int read = dev.Read(0, buf);
        Assert.Equal(data.Length, read);
        Assert.True(buf.SequenceEqual(data));
    }

    [Fact]
    public void MemoryBlockDevice_ToArray_And_AsSpan_ReflectWrittenData()
    {
        byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
        MemoryBlockDevice dev = new(payload);
        _devices.Add(dev);

        Assert.Equal(payload.Length, dev.Length);
        Assert.Equal(payload, dev.ToArray());
        Assert.True(dev.AsSpan().SequenceEqual(payload));

        // Mutating written data via Write should be reflected
        dev.Write(2, "\t\t"u8);
        byte[] expected = new byte[] { 1, 2, 9, 9, 5 };
        Assert.Equal(expected, dev.ToArray());
    }

    [Fact]
    public void MemoryBlockDevice_Growth_OnWriteBeyondCapacity()
    {
        MemoryBlockDevice dev = new();
        _devices.Add(dev);

        byte[] big = new byte[5000];
        new Random(42).NextBytes(big);
        dev.Write(0, big);
        Assert.Equal(5000, dev.Length);

        // Write at offset beyond current length should grow
        byte[] extra = new byte[] { 0xAA, 0xBB };
        dev.Write(6000, extra);
        Assert.Equal(6002, dev.Length);

        Span<byte> buf = stackalloc byte[2];
        int r = dev.Read(6000, buf);
        Assert.Equal(2, r);
        Assert.Equal(extra, buf.ToArray());

        // Gap should be zero-filled
        Span<byte> gap = stackalloc byte[1000];
        dev.Read(5000, gap);
        Assert.True(gap.ToArray().All(static b => b == 0));
    }

    [Fact]
    public void MemoryBlockDevice_Read_OutOfRange_ReturnsZero()
    {
        MemoryBlockDevice dev = new(new byte[] { 1, 2, 3 });
        _devices.Add(dev);

        Span<byte> buf = stackalloc byte[10];
        buf.Fill(0xFF);
        int read = dev.Read(10, buf);
        Assert.Equal(0, read);
        // When offset >= Length, method returns 0 without touching buffer? Implementation returns 0 immediately.
        // Ensure reading at exactly Length also returns 0
        int read2 = dev.Read(dev.Length, buf);
        Assert.Equal(0, read2);
    }

    [Fact]
    public void MemoryBlockDevice_Read_NegativeOffset_Throws()
    {
        MemoryBlockDevice dev = new(new byte[] { 1, 2, 3 });
        _devices.Add(dev);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<byte> buf = stackalloc byte[5];
            dev.Read(-1, buf);
        });
    }

    [Fact]
    public void MemoryBlockDevice_Write_NegativeOffset_Throws()
    {
        MemoryBlockDevice dev = new();
        _devices.Add(dev);

        Assert.Throws<ArgumentOutOfRangeException>(() => dev.Write(-1, new byte[] { 1 }));
    }

    [Fact]
    public void MemoryBlockDevice_Dispose_DoesNotThrow()
    {
        MemoryBlockDevice dev = new(new byte[] { 1, 2 });
        dev.Dispose();
        dev.Dispose(); // second dispose should be safe
    }

    [Fact]
    public void MemoryBlockDevice_Ctor_WithCapacity_ZeroFilled()
    {
        MemoryBlockDevice dev = new(1024);
        _devices.Add(dev);

        Assert.Equal(1024, dev.Length);
        Span<byte> buf = stackalloc byte[1024];
        int r = dev.Read(0, buf);
        Assert.Equal(1024, r);
        Assert.True(buf.ToArray().All(static b => b == 0));
    }

    [Fact]
    public void MemoryBlockDevice_Ctor_HugeCapacity_ThrowsArgumentOutOfRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBlockDevice((long)Array.MaxLength + 1));

    [Fact]
    public void MemoryBlockDevice_Write_BeyondMaxLength_ThrowsInvalidOperation()
    {
        MemoryBlockDevice dev = new();
        _devices.Add(dev);

        // No allocation happens: the request is rejected before growth math.
        Assert.Throws<InvalidOperationException>(() => dev.Write(Array.MaxLength, new byte[1]));
    }

    // ---- FileBlockDevice ----

    [Fact]
    public void FileBlockDevice_ReadWrite_RoundTripViaTempFile()
    {
        string tmpDir = CreateTempDir();
        string path = Path.Combine(tmpDir, "filedev.bin");
        _tempFiles.Add(path);

        using (FileStream fs = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536))
        {
            FileBlockDevice dev = new(fs, leaveOpen: false);
            byte[] data = "file block device payload"u8.ToArray();
            dev.Write(0, data);
            Assert.Equal(data.Length, dev.Length);

            Span<byte> buf = stackalloc byte[data.Length];
            int read = dev.Read(0, buf);
            Assert.Equal(data.Length, read);
            Assert.True(buf.SequenceEqual(data));
            dev.Dispose();
        }

        // Reopen via path ctor and verify persistence
        FileBlockDevice dev2 = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _devices.Add(dev2);
        Span<byte> buf2 = stackalloc byte[5];
        int r2 = dev2.Read(0, buf2);
        Assert.Equal(5, r2);
        Assert.Equal("file "u8.ToArray(), buf2.ToArray());
    }

    [Fact]
    public void FileBlockDevice_Length_ReflectsFileSize()
    {
        string tmpDir = CreateTempDir();
        string path = Path.Combine(tmpDir, "len.bin");
        _tempFiles.Add(path);

        File.WriteAllBytes(path, new byte[2048]);
        FileBlockDevice dev = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        _devices.Add(dev);

        Assert.Equal(2048, dev.Length);
        dev.Write(2048, new byte[100]);
        // Need to flush; FileBlockDevice.Write seeks and writes via BaseStream
        dev.BaseStream.Flush();
        Assert.Equal(2148, dev.Length);
    }

    [Fact]
    public void FileBlockDevice_Dispose_ClosesStream_WhenNotLeaveOpen()
    {
        string tmpDir = CreateTempDir();
        string path = Path.Combine(tmpDir, "dispose.bin");
        File.WriteAllBytes(path, new byte[10]);

        FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        FileBlockDevice dev = new(fs, leaveOpen: false);
        dev.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fs.ReadByte());

        // leaveOpen = true should keep stream open
        FileStream fs2 = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        FileBlockDevice dev2 = new(fs2, leaveOpen: true);
        dev2.Dispose();
        // fs2 should still be usable
        Assert.True(fs2.CanRead);
        fs2.Dispose();
    }

    // ---- OffsetBlockDevice ----

    [Fact]
    public void OffsetBlockDevice_WrapsWithOffset_ReadCorrectly()
    {
        MemoryBlockDevice inner = new("0123456789"u8);
        _devices.Add(inner);

        OffsetBlockDevice offsetDev = new(inner, 5, leaveOpen: true);
        _devices.Add(offsetDev);

        Span<byte> buf = stackalloc byte[5];
        int r = offsetDev.Read(0, buf);
        Assert.Equal(5, r);
        Assert.Equal("56789"u8.ToArray(), buf.ToArray());

        // Reading beyond should clamp
        Span<byte> buf2 = stackalloc byte[10];
        int r2 = offsetDev.Read(0, buf2);
        Assert.Equal(5, r2); // only 5 bytes available after offset
    }

    [Fact]
    public void OffsetBlockDevice_Length_IsInnerMinusOffset()
    {
        MemoryBlockDevice inner = new(100);
        _devices.Add(inner);

        OffsetBlockDevice dev0 = new(inner, 0, leaveOpen: true);
        _devices.Add(dev0);
        Assert.Equal(100, dev0.Length);

        OffsetBlockDevice dev10 = new(inner, 10, leaveOpen: true);
        _devices.Add(dev10);
        Assert.Equal(90, dev10.Length);

        OffsetBlockDevice devBeyond = new(inner, 200, leaveOpen: true);
        _devices.Add(devBeyond);
        Assert.Equal(0, devBeyond.Length);
    }

    [Fact]
    public void OffsetBlockDevice_Write_ForwardsToInnerAtOffset()
    {
        MemoryBlockDevice inner = new(new byte[20]);
        _devices.Add(inner);

        OffsetBlockDevice offsetDev = new(inner, 10, leaveOpen: true);
        _devices.Add(offsetDev);

        offsetDev.Write(0, new byte[] { 0xAA, 0xBB, 0xCC });
        // Verify inner has data at absolute offset 10
        Span<byte> check = stackalloc byte[3];
        inner.Read(10, check);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, check.ToArray());

        // Verify read via offset device
        Span<byte> buf = stackalloc byte[3];
        offsetDev.Read(0, buf);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, buf.ToArray());
    }

    [Fact]
    public void OffsetBlockDevice_Probe_FindsHeaderAtZero()
    {
        // Build a fake XISO with header at offset 0x10000
        const int size = Constants.HeaderOffset + Constants.HeaderDataLength + 1024;
        byte[] data = new byte[size];
        Encoding.ASCII.GetBytes(Constants.HeaderData).CopyTo(data.AsSpan(Constants.HeaderOffset));
        MemoryBlockDevice inner = new(data);
        _devices.Add(inner);

        OffsetBlockDevice probed = OffsetBlockDevice.Probe(inner, "test.iso");
        _devices.Add(probed);
        Assert.Equal(0, probed.Offset);
        Assert.Equal(inner.Length, probed.Length);
    }

    [Fact]
    public void OffsetBlockDevice_Probe_ThrowsWhenNoHeaderFound()
    {
        MemoryBlockDevice inner = new(new byte[Constants.HeaderOffset + 100]);
        _devices.Add(inner);

        Assert.Throws<XisoFormatException>(() => OffsetBlockDevice.Probe(inner, "missing.iso"));
    }

    [Fact]
    public void OffsetBlockDevice_NegativeOffset_Throws()
    {
        MemoryBlockDevice inner = new(new byte[10]);
        _devices.Add(inner);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffsetBlockDevice(inner, -1));
    }

    // ---- CisoBlockDevice ----

    [Fact]
    public void CisoBlockDevice_ReadBlockZero_MatchesOriginalIso()
    {
        string isoPath = CreateTempIso();
        byte[] isoBytes = File.ReadAllBytes(isoPath);

        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ciso_dev.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);
        _tempFiles.Add(csoPath);

        using FileStream fs = new(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        CisoBlockDevice cisoDev = new(fs, leaveOpen: true);
        _devices.Add(cisoDev);
        // Also test string ctor
        CisoBlockDevice cisoDev2 = new(csoPath);
        _devices.Add(cisoDev2);

        Assert.Equal(isoBytes.Length, cisoDev.Length);
        Assert.Equal(isoBytes.Length, cisoDev2.Length);

        Span<byte> buf = stackalloc byte[2048];
        int read = cisoDev.Read(0, buf);
        Assert.Equal(Math.Min(2048, isoBytes.Length), read);
        Assert.True(buf[..read].SequenceEqual(isoBytes.AsSpan(0, read)));

        // Read via second device (string ctor) block 0 as well
        Span<byte> buf2 = stackalloc byte[512];
        int read2 = cisoDev2.Read(0, buf2);
        Assert.Equal(512, read2);
        Assert.True(buf2.SequenceEqual(isoBytes.AsSpan(0, 512)));
    }

    [Fact]
    public void CisoBlockDevice_ReadBlockOne_And_CrossSectorMatchesDecompressed()
    {
        string isoPath = CreateTempIso();
        byte[] isoBytes = File.ReadAllBytes(isoPath);

        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ciso_dev2.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);
        _tempFiles.Add(csoPath);

        using FileStream fs = new(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        CisoBlockDevice cisoDev = new(fs, leaveOpen: true);
        _devices.Add(cisoDev);

        if (isoBytes.Length > 2048)
        {
            Span<byte> block1 = stackalloc byte[2048];
            int r1 = cisoDev.Read(2048, block1);
            Assert.Equal(2048, r1);
            Assert.True(block1.SequenceEqual(isoBytes.AsSpan(2048, 2048)));
        }

        // Cross-sector read (3000 bytes from offset 1000)
        const long offset = 1000;
        const int len = 3000;
        if (offset + len <= isoBytes.Length)
        {
            byte[] buf = new byte[len];
            int r = cisoDev.Read(offset, buf.AsSpan());
            Assert.Equal(len, r);
            Assert.True(buf.AsSpan().SequenceEqual(isoBytes.AsSpan((int)offset, len)));
        }
    }

    [Fact]
    public void CisoBlockDevice_Write_ThrowsNotSupported()
    {
        string isoPath = CreateTempIso();
        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ciso_wr.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        CisoBlockDevice dev = new(csoPath);
        _devices.Add(dev);

        Assert.Throws<NotSupportedException>(() => dev.Write(0, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void CisoBlockDevice_Read_OutOfRange_ReturnsZero()
    {
        string isoPath = CreateTempIso();
        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ciso_oor.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        CisoBlockDevice dev = new(csoPath);
        _devices.Add(dev);

        Span<byte> buf = stackalloc byte[10];
        int r = dev.Read(dev.Length + 100, buf);
        Assert.Equal(0, r);

        int r2 = dev.Read(dev.Length, buf);
        Assert.Equal(0, r2);
    }

    [Fact]
    public void CisoBlockDevice_Length_EqualsUncompressedSize_And_Sha256Matches()
    {
        string isoPath = CreateTempIso();
        byte[] isoBytes = File.ReadAllBytes(isoPath);
        byte[] expectedHash = SHA256.HashData(isoBytes);

        string csoDir = CreateTempDir();
        string csoPath = Path.Combine(csoDir, "ciso_len.cso");
        int rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        CisoBlockDevice dev = new(csoPath);
        _devices.Add(dev);
        Assert.Equal(isoBytes.Length, dev.Length);

        // Read entire image via block device and hash
        byte[] all = new byte[dev.Length];
        int totalRead = 0;
        while (totalRead < all.Length)
        {
            int toRead = Math.Min(4096, all.Length - totalRead);
            int r = dev.Read(totalRead, all.AsSpan(totalRead, toRead));
            if (r == 0) break;
            totalRead += r;
        }

        Assert.Equal(all.Length, totalRead);
        Assert.Equal(expectedHash, SHA256.HashData(all));
    }

    private static string WriteFakeCso(string dir, string name, ulong claimedSize)
    {
        string path = Path.Combine(dir, name);
        Span<byte> hdr = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], CisoReader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..8], CisoReader.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr[8..16], claimedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..20], (uint)CisoReader.BlockSize);
        hdr[20] = CisoWriter.VersionDeflate;
        File.WriteAllBytes(path, hdr.ToArray());
        return path;
    }

    [Fact]
    public void CisoBlockDevice_BadMagic_ThrowsAndReleasesFileHandle()
    {
        string dir = CreateTempDir();
        string path = Path.Combine(dir, "bad.cso");
        File.WriteAllBytes(path, new byte[64]);
        _tempFiles.Add(path);

        Assert.Throws<InvalidDataException>(() => new CisoBlockDevice(path));

        // No leaked handle: an exclusive open must succeed after the failed ctor.
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void CisoBlockDevice_HugeClaimedSize_ThrowsInvalidDataException()
    {
        string dir = CreateTempDir();
        string path = WriteFakeCso(dir, "huge.cso", (ulong)long.MaxValue + 1);
        _tempFiles.Add(path);

        // Must not wrap Length negative (OverflowException) — documented InvalidDataException.
        Assert.Throws<InvalidDataException>(() => new CisoBlockDevice(path));
    }

    [Fact]
    public void FileBlockDevice_ViaPathCtor_ReadsCorrectly()
    {
        string tmpDir = CreateTempDir();
        string path = Path.Combine(tmpDir, "pathctor.bin");
        byte[] payload = new byte[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        File.WriteAllBytes(path, payload);

        FileBlockDevice dev = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _devices.Add(dev);

        Assert.Equal(256, dev.Length);
        Span<byte> buf = stackalloc byte[256];
        int r = dev.Read(0, buf);
        Assert.Equal(256, r);
        Assert.True(buf.SequenceEqual(payload));
    }

    [Fact]
    public void VerifyXiso_BlockDevice_ZeroRootSizeWithSector_ThrowsLikeStream()
    {
        // BUG-LIB-037: the IBlockDevice overload omitted the rootDirSize == 0
        // check the Stream overload performs — it returned a tuple for corrupt.
        using MemoryBlockDevice dev = new(BuildCorruptHeader(33, 0));

        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "mem"));
    }

    [Fact]
    public void VerifyXiso_BlockDevice_OversizedRoot_ThrowsLikeStream()
    {
        // BUG-LIB-037: same parity gap for the availableBytes check.
        using MemoryBlockDevice dev = new(BuildCorruptHeader(33, 0x100000));

        Assert.Throws<XisoFormatException>(() => XisoReader.VerifyXiso(dev, "mem"));
    }

    private static byte[] BuildCorruptHeader(uint rootSector, uint rootSize)
    {
        byte[] bytes = new byte[64 * Constants.SectorSize];
        byte[] magic = Encoding.ASCII.GetBytes(Constants.HeaderData);
        const int header = Constants.HeaderOffset;
        magic.CopyTo(bytes.AsSpan(header, magic.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 20, 4), rootSector);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 24, 4), rootSize);
        magic.CopyTo(bytes.AsSpan(
            header + 20 + 4 + 4 + Constants.FileTimeSize + Constants.UnusedSize, magic.Length));
        return bytes;
    }
}
