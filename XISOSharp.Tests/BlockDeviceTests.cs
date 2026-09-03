using System.Security.Cryptography;
using System.Text;
using XISOSharp.BlockDevice;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for IBlockDevice implementations: MemoryBlockDevice, FileBlockDevice,
/// OffsetBlockDevice and CisoBlockDevice.
/// </summary>
[Collection("Sequential")]
public class BlockDeviceTests : IDisposable
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    private static readonly string SourceDir = Path.Combine(TestDataRoot, "source");

    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];
    private readonly List<IBlockDevice> _devices = [];

    public void Dispose()
    {
        foreach (var d in _devices)
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

        foreach (var dir in _tempDirs)
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

        foreach (var f in _tempFiles)
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
        return outPath;
    }

    // ---- MemoryBlockDevice ----

    [Fact]
    public void MemoryBlockDevice_WriteAndRead_Basic()
    {
        var dev = new MemoryBlockDevice();
        _devices.Add(dev);

        var data = "hello world"u8.ToArray();
        dev.Write(0, data);

        Assert.Equal(data.Length, dev.Length);

        Span<byte> buf = stackalloc byte[data.Length];
        var read = dev.Read(0, buf);
        Assert.Equal(data.Length, read);
        Assert.True(buf.SequenceEqual(data));
    }

    [Fact]
    public void MemoryBlockDevice_ToArray_And_AsSpan_ReflectWrittenData()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var dev = new MemoryBlockDevice(payload);
        _devices.Add(dev);

        Assert.Equal(payload.Length, dev.Length);
        Assert.Equal(payload, dev.ToArray());
        Assert.True(dev.AsSpan().SequenceEqual(payload));

        // Mutating written data via Write should be reflected
        dev.Write(2, "\t\t"u8);
        var expected = new byte[] { 1, 2, 9, 9, 5 };
        Assert.Equal(expected, dev.ToArray());
    }

    [Fact]
    public void MemoryBlockDevice_Growth_OnWriteBeyondCapacity()
    {
        var dev = new MemoryBlockDevice();
        _devices.Add(dev);

        var big = new byte[5000];
        new Random(42).NextBytes(big);
        dev.Write(0, big);
        Assert.Equal(5000, dev.Length);

        // Write at offset beyond current length should grow
        var extra = new byte[] { 0xAA, 0xBB };
        dev.Write(6000, extra);
        Assert.Equal(6002, dev.Length);

        Span<byte> buf = stackalloc byte[2];
        var r = dev.Read(6000, buf);
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
        var dev = new MemoryBlockDevice(new byte[] { 1, 2, 3 });
        _devices.Add(dev);

        Span<byte> buf = stackalloc byte[10];
        buf.Fill(0xFF);
        var read = dev.Read(10, buf);
        Assert.Equal(0, read);
        // When offset >= Length, method returns 0 without touching buffer? Implementation returns 0 immediately.
        // Ensure reading at exactly Length also returns 0
        var read2 = dev.Read(dev.Length, buf);
        Assert.Equal(0, read2);
    }

    [Fact]
    public void MemoryBlockDevice_Read_NegativeOffset_Throws()
    {
        var dev = new MemoryBlockDevice(new byte[] { 1, 2, 3 });
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
        var dev = new MemoryBlockDevice();
        _devices.Add(dev);

        Assert.Throws<ArgumentOutOfRangeException>(() => dev.Write(-1, new byte[] { 1 }));
    }

    [Fact]
    public void MemoryBlockDevice_Dispose_DoesNotThrow()
    {
        var dev = new MemoryBlockDevice(new byte[] { 1, 2 });
        dev.Dispose();
        dev.Dispose(); // second dispose should be safe
    }

    [Fact]
    public void MemoryBlockDevice_Ctor_WithCapacity_ZeroFilled()
    {
        var dev = new MemoryBlockDevice(1024);
        _devices.Add(dev);

        Assert.Equal(1024, dev.Length);
        Span<byte> buf = stackalloc byte[1024];
        var r = dev.Read(0, buf);
        Assert.Equal(1024, r);
        Assert.True(buf.ToArray().All(static b => b == 0));
    }

    // ---- FileBlockDevice ----

    [Fact]
    public void FileBlockDevice_ReadWrite_RoundTripViaTempFile()
    {
        var tmpDir = CreateTempDir();
        var path = Path.Combine(tmpDir, "filedev.bin");
        _tempFiles.Add(path);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536))
        {
            var dev = new FileBlockDevice(fs, leaveOpen: false);
            var data = "file block device payload"u8.ToArray();
            dev.Write(0, data);
            Assert.Equal(data.Length, dev.Length);

            Span<byte> buf = stackalloc byte[data.Length];
            var read = dev.Read(0, buf);
            Assert.Equal(data.Length, read);
            Assert.True(buf.SequenceEqual(data));
            dev.Dispose();
        }

        // Reopen via path ctor and verify persistence
        var dev2 = new FileBlockDevice(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _devices.Add(dev2);
        Span<byte> buf2 = stackalloc byte[5];
        var r2 = dev2.Read(0, buf2);
        Assert.Equal(5, r2);
        Assert.Equal("file "u8.ToArray(), buf2.ToArray());
    }

    [Fact]
    public void FileBlockDevice_Length_ReflectsFileSize()
    {
        var tmpDir = CreateTempDir();
        var path = Path.Combine(tmpDir, "len.bin");
        _tempFiles.Add(path);

        File.WriteAllBytes(path, new byte[2048]);
        var dev = new FileBlockDevice(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
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
        var tmpDir = CreateTempDir();
        var path = Path.Combine(tmpDir, "dispose.bin");
        File.WriteAllBytes(path, new byte[10]);

        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        var dev = new FileBlockDevice(fs, leaveOpen: false);
        dev.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fs.ReadByte());

        // leaveOpen = true should keep stream open
        var fs2 = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        var dev2 = new FileBlockDevice(fs2, leaveOpen: true);
        dev2.Dispose();
        // fs2 should still be usable
        Assert.True(fs2.CanRead);
        fs2.Dispose();
    }

    // ---- OffsetBlockDevice ----

    [Fact]
    public void OffsetBlockDevice_WrapsWithOffset_ReadCorrectly()
    {
        var inner = new MemoryBlockDevice("0123456789"u8);
        _devices.Add(inner);

        var offsetDev = new OffsetBlockDevice(inner, 5, leaveOpen: true);
        _devices.Add(offsetDev);

        Span<byte> buf = stackalloc byte[5];
        var r = offsetDev.Read(0, buf);
        Assert.Equal(5, r);
        Assert.Equal("56789"u8.ToArray(), buf.ToArray());

        // Reading beyond should clamp
        Span<byte> buf2 = stackalloc byte[10];
        var r2 = offsetDev.Read(0, buf2);
        Assert.Equal(5, r2); // only 5 bytes available after offset
    }

    [Fact]
    public void OffsetBlockDevice_Length_IsInnerMinusOffset()
    {
        var inner = new MemoryBlockDevice(100);
        _devices.Add(inner);

        var dev0 = new OffsetBlockDevice(inner, 0, leaveOpen: true);
        _devices.Add(dev0);
        Assert.Equal(100, dev0.Length);

        var dev10 = new OffsetBlockDevice(inner, 10, leaveOpen: true);
        _devices.Add(dev10);
        Assert.Equal(90, dev10.Length);

        var devBeyond = new OffsetBlockDevice(inner, 200, leaveOpen: true);
        _devices.Add(devBeyond);
        Assert.Equal(0, devBeyond.Length);
    }

    [Fact]
    public void OffsetBlockDevice_Write_ForwardsToInnerAtOffset()
    {
        var inner = new MemoryBlockDevice(new byte[20]);
        _devices.Add(inner);

        var offsetDev = new OffsetBlockDevice(inner, 10, leaveOpen: true);
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
        var data = new byte[size];
        Encoding.ASCII.GetBytes(Constants.HeaderData).CopyTo(data.AsSpan(Constants.HeaderOffset));
        var inner = new MemoryBlockDevice(data);
        _devices.Add(inner);

        var probed = OffsetBlockDevice.Probe(inner, "test.iso");
        _devices.Add(probed);
        Assert.Equal(0, probed.Offset);
        Assert.Equal(inner.Length, probed.Length);
    }

    [Fact]
    public void OffsetBlockDevice_Probe_ThrowsWhenNoHeaderFound()
    {
        var inner = new MemoryBlockDevice(new byte[Constants.HeaderOffset + 100]);
        _devices.Add(inner);

        Assert.Throws<XisoFormatException>(() => OffsetBlockDevice.Probe(inner, "missing.iso"));
    }

    [Fact]
    public void OffsetBlockDevice_NegativeOffset_Throws()
    {
        var inner = new MemoryBlockDevice(new byte[10]);
        _devices.Add(inner);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OffsetBlockDevice(inner, -1));
    }

    // ---- CisoBlockDevice ----

    [Fact]
    public void CisoBlockDevice_ReadBlockZero_MatchesOriginalIso()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "ciso_dev.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);
        _tempFiles.Add(csoPath);

        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var cisoDev = new CisoBlockDevice(fs, leaveOpen: true);
        _devices.Add(cisoDev);
        // Also test string ctor
        var cisoDev2 = new CisoBlockDevice(csoPath);
        _devices.Add(cisoDev2);

        Assert.Equal(isoBytes.Length, cisoDev.Length);
        Assert.Equal(isoBytes.Length, cisoDev2.Length);

        Span<byte> buf = stackalloc byte[2048];
        var read = cisoDev.Read(0, buf);
        Assert.Equal(Math.Min(2048, isoBytes.Length), read);
        Assert.True(buf[..read].SequenceEqual(isoBytes.AsSpan(0, read)));

        // Read via second device (string ctor) block 0 as well
        Span<byte> buf2 = stackalloc byte[512];
        var read2 = cisoDev2.Read(0, buf2);
        Assert.Equal(512, read2);
        Assert.True(buf2.SequenceEqual(isoBytes.AsSpan(0, 512)));
    }

    [Fact]
    public void CisoBlockDevice_ReadBlockOne_And_CrossSectorMatchesDecompressed()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "ciso_dev2.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);
        _tempFiles.Add(csoPath);

        using var fs = new FileStream(csoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var cisoDev = new CisoBlockDevice(fs, leaveOpen: true);
        _devices.Add(cisoDev);

        if (isoBytes.Length > 2048)
        {
            Span<byte> block1 = stackalloc byte[2048];
            var r1 = cisoDev.Read(2048, block1);
            Assert.Equal(2048, r1);
            Assert.True(block1.SequenceEqual(isoBytes.AsSpan(2048, 2048)));
        }

        // Cross-sector read (3000 bytes from offset 1000)
        const long offset = 1000;
        const int len = 3000;
        if (offset + len <= isoBytes.Length)
        {
            var buf = new byte[len];
            var r = cisoDev.Read(offset, buf.AsSpan());
            Assert.Equal(len, r);
            Assert.True(buf.AsSpan().SequenceEqual(isoBytes.AsSpan((int)offset, len)));
        }
    }

    [Fact]
    public void CisoBlockDevice_Write_ThrowsNotSupported()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "ciso_wr.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var dev = new CisoBlockDevice(csoPath);
        _devices.Add(dev);

        Assert.Throws<NotSupportedException>(() => dev.Write(0, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void CisoBlockDevice_Read_OutOfRange_ReturnsZero()
    {
        var isoPath = CreateTempIso();
        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "ciso_oor.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var dev = new CisoBlockDevice(csoPath);
        _devices.Add(dev);

        Span<byte> buf = stackalloc byte[10];
        var r = dev.Read(dev.Length + 100, buf);
        Assert.Equal(0, r);

        var r2 = dev.Read(dev.Length, buf);
        Assert.Equal(0, r2);
    }

    [Fact]
    public void CisoBlockDevice_Length_EqualsUncompressedSize_And_Sha256Matches()
    {
        var isoPath = CreateTempIso();
        var isoBytes = File.ReadAllBytes(isoPath);
        var expectedHash = SHA256.HashData(isoBytes);

        var csoDir = CreateTempDir();
        var csoPath = Path.Combine(csoDir, "ciso_len.cso");
        var rc = CisoWriter.CompressToCso(isoPath, csoPath, level: 6);
        Assert.Equal(0, rc);

        var dev = new CisoBlockDevice(csoPath);
        _devices.Add(dev);
        Assert.Equal(isoBytes.Length, dev.Length);

        // Read entire image via block device and hash
        var all = new byte[dev.Length];
        var totalRead = 0;
        while (totalRead < all.Length)
        {
            var toRead = Math.Min(4096, all.Length - totalRead);
            var r = dev.Read(totalRead, all.AsSpan(totalRead, toRead));
            if (r == 0) break;
            totalRead += r;
        }

        Assert.Equal(all.Length, totalRead);
        Assert.Equal(expectedHash, SHA256.HashData(all));
    }

    [Fact]
    public void FileBlockDevice_ViaPathCtor_ReadsCorrectly()
    {
        var tmpDir = CreateTempDir();
        var path = Path.Combine(tmpDir, "pathctor.bin");
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        File.WriteAllBytes(path, payload);

        var dev = new FileBlockDevice(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _devices.Add(dev);

        Assert.Equal(256, dev.Length);
        Span<byte> buf = stackalloc byte[256];
        var r = dev.Read(0, buf);
        Assert.Equal(256, r);
        Assert.True(buf.SequenceEqual(payload));
    }
}