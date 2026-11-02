using XISOSharp.BlockDevice;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="BlockDeviceStream"/>: read/seek behavior, EOF handling,
/// read-only guards, disposal, and <c>leaveOpen</c> semantics.
/// </summary>
public class BlockDeviceStreamTests
{
    private static BlockDeviceStream CreateStream(byte[] data, bool leaveOpen = true) =>
        new(new MemoryBlockDevice(data), leaveOpen);

    /// <summary>Verifies sequential reads return device bytes and advance the position.</summary>
    [Fact]
    public void Read_ReturnsDeviceBytes_AndAdvancesPosition()
    {
        using BlockDeviceStream stream = CreateStream([1, 2, 3, 4, 5]);
        byte[] buffer = new byte[3];
        Assert.Equal(3, stream.Read(buffer, 0, 3));
        Assert.Equal([1, 2, 3], buffer);
        Assert.Equal(3, stream.Position);

        Assert.Equal(2, stream.Read(buffer, 0, 3));
        Assert.Equal([4, 5], buffer[..2]);
        Assert.Equal(5, stream.Position);
    }

    /// <summary>Verifies reads at or past the end return zero bytes.</summary>
    [Fact]
    public void Read_AtEnd_ReturnsZero()
    {
        using BlockDeviceStream stream = CreateStream([1, 2]);
        stream.Seek(0, SeekOrigin.End);
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
    }

    /// <summary>Verifies an empty read buffer is a no-op.</summary>
    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        using BlockDeviceStream stream = CreateStream([1, 2]);
        Assert.Equal(0, stream.Read([], 0, 0));
    }

    /// <summary>Verifies the span overload reads correctly.</summary>
    [Fact]
    public void Read_SpanOverload_Reads()
    {
        using BlockDeviceStream stream = CreateStream([9, 8, 7]);
        Span<byte> buffer = stackalloc byte[2];
        Assert.Equal(2, stream.Read(buffer));
        Assert.Equal([9, 8], buffer.ToArray());
    }

    /// <summary>Verifies all seek origins resolve correctly.</summary>
    [Fact]
    public void Seek_AllOrigins_Resolve()
    {
        using BlockDeviceStream stream = CreateStream([1, 2, 3, 4]);
        Assert.Equal(2, stream.Seek(2, SeekOrigin.Begin));
        Assert.Equal(3, stream.Seek(1, SeekOrigin.Current));
        Assert.Equal(1, stream.Seek(-3, SeekOrigin.End));
    }

    /// <summary>Verifies an unknown seek origin is rejected.</summary>
    [Fact]
    public void Seek_InvalidOrigin_Throws()
    {
        using BlockDeviceStream stream = CreateStream([1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
    }

    /// <summary>Verifies negative positions are rejected.</summary>
    [Fact]
    public void Position_Negative_Throws()
    {
        using BlockDeviceStream stream = CreateStream([1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }

    /// <summary>Verifies the position setter moves the read cursor.</summary>
    [Fact]
    public void Position_Set_MovesCursor()
    {
        using BlockDeviceStream stream = CreateStream([1, 2, 3]);
        stream.Position = 2;
        Assert.Equal(3, stream.ReadByte());
    }

    /// <summary>Verifies length and capability flags mirror the device.</summary>
    [Fact]
    public void Length_And_CanFlags()
    {
        using BlockDeviceStream stream = CreateStream([1, 2, 3, 4, 5, 6]);
        Assert.Equal(6, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    /// <summary>Verifies the stream is read-only.</summary>
    [Fact]
    public void Write_And_SetLength_ThrowNotSupported()
    {
        using BlockDeviceStream stream = CreateStream([1]);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    /// <summary>Verifies flush is a no-op.</summary>
    [Fact]
    public void Flush_DoesNotThrow()
    {
        using BlockDeviceStream stream = CreateStream([1]);
        stream.Flush();
    }

    /// <summary>Verifies a null device is rejected.</summary>
    [Fact]
    public void Ctor_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BlockDeviceStream(null!));
    }

    /// <summary>Verifies operations after disposal throw <see cref="ObjectDisposedException"/>.</summary>
    [Fact]
    public void AfterDispose_OperationsThrow()
    {
        BlockDeviceStream stream = CreateStream([1, 2]);
        stream.Dispose();
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
    }

    /// <summary>Verifies <c>leaveOpen: true</c> keeps the backing device usable.</summary>
    [Fact]
    public void Dispose_LeaveOpenTrue_DeviceStaysUsable()
    {
        MemoryBlockDevice device = new([5, 6]);
        BlockDeviceStream stream = new(device, leaveOpen: true);
        stream.Dispose();
        Span<byte> buffer = stackalloc byte[2];
        Assert.Equal(2, device.Read(0, buffer));
        Assert.Equal([5, 6], buffer.ToArray());
    }

    /// <summary>Verifies disposing with <c>leaveOpen: false</c> does not throw.</summary>
    [Fact]
    public void Dispose_LeaveOpenFalse_DoesNotThrow()
    {
        BlockDeviceStream stream = CreateStream([1], leaveOpen: false);
        stream.Dispose();
    }
}
