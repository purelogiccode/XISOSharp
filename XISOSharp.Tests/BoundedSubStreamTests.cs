using XISOSharp.BlockDevice;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for the internal <see cref="BoundedSubStream"/>: window clamping,
/// seek behavior, read-only guards, disposal callbacks, and ownership semantics.
/// </summary>
public class BoundedSubStreamTests
{
    private static MemoryStream CreateParent()
    {
        byte[] data = new byte[100];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        return new MemoryStream(data);
    }

    /// <summary>Verifies reads return parent bytes inside the window and clamp at its end.</summary>
    [Fact]
    public void Read_ClampsToWindow()
    {
        using MemoryStream parent = CreateParent();
        using BoundedSubStream window = new(parent, start: 10, length: 5, ownsParent: false);

        byte[] buffer = new byte[8];
        Assert.Equal(5, window.Read(buffer, 0, buffer.Length));
        Assert.Equal([10, 11, 12, 13, 14], buffer[..5]);
        Assert.Equal(5, window.Position);
        Assert.Equal(0, window.Read(buffer, 0, buffer.Length));
    }

    /// <summary>Verifies reads from the parent continue to work after the window closes.</summary>
    [Fact]
    public void Read_DoesNotAdvanceParentStatePermanently()
    {
        using MemoryStream parent = CreateParent();
        using (BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: false))
        {
            Assert.Equal(4, window.Read(new byte[4], 0, 4));
        }

        parent.Position = 0;
        Assert.Equal(0, parent.ReadByte());
    }

    /// <summary>Verifies all seek origins resolve within the window.</summary>
    [Fact]
    public void Seek_AllOrigins()
    {
        using MemoryStream parent = CreateParent();
        using BoundedSubStream window = new(parent, start: 10, length: 20, ownsParent: false);

        Assert.Equal(5, window.Seek(5, SeekOrigin.Begin));
        Assert.Equal(7, window.Seek(2, SeekOrigin.Current));
        Assert.Equal(20, window.Seek(0, SeekOrigin.End));
        Assert.Equal(0, window.Position = 0);
    }

    /// <summary>Verifies seeking before the window start is rejected.</summary>
    [Fact]
    public void Seek_BeforeStart_Throws()
    {
        using MemoryStream parent = CreateParent();
        using BoundedSubStream window = new(parent, start: 10, length: 20, ownsParent: false);
        Assert.Throws<IOException>(() => window.Seek(-1, SeekOrigin.Begin));
    }

    /// <summary>Verifies an unknown seek origin is rejected.</summary>
    [Fact]
    public void Seek_InvalidOrigin_Throws()
    {
        using MemoryStream parent = CreateParent();
        using BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Seek(0, (SeekOrigin)99));
    }

    /// <summary>Verifies the stream is read-only and length reflects the window.</summary>
    [Fact]
    public void WriteAndSetLength_Throw_AndLengthIsWindow()
    {
        using MemoryStream parent = CreateParent();
        using BoundedSubStream window = new(parent, start: 10, length: 20, ownsParent: false);

        Assert.Equal(20, window.Length);
        Assert.True(window.CanRead);
        Assert.True(window.CanSeek);
        Assert.False(window.CanWrite);
        Assert.Throws<NotSupportedException>(() => window.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => window.SetLength(1));
        window.Flush();
    }

    /// <summary>Verifies the disposal callback fires exactly once.</summary>
    [Fact]
    public void Dispose_InvokesCallbackOnce()
    {
        using MemoryStream parent = CreateParent();
        int calls = 0;
        BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: false, onDisposed: () => calls++);

        window.Dispose();
        window.Dispose();

        Assert.Equal(1, calls);
    }

    /// <summary>Verifies operations after disposal throw <see cref="ObjectDisposedException"/>.</summary>
    [Fact]
    public void AfterDispose_OperationsThrow()
    {
        using MemoryStream parent = CreateParent();
        BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: false);
        window.Dispose();

        Assert.False(window.CanRead);
        Assert.False(window.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => window.Read(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => window.Seek(0, SeekOrigin.Begin));
    }

    /// <summary>Verifies <c>ownsParent: false</c> leaves the parent open.</summary>
    [Fact]
    public void Dispose_LeaveParentOpen()
    {
        MemoryStream parent = CreateParent();
        BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: false);
        window.Dispose();

        Assert.True(parent.CanRead);
        parent.Dispose();
    }

    /// <summary>Verifies <c>ownsParent: true</c> disposes the parent.</summary>
    [Fact]
    public void Dispose_OwnsParent_ClosesIt()
    {
        MemoryStream parent = CreateParent();
        BoundedSubStream window = new(parent, start: 0, length: 4, ownsParent: true);
        window.Dispose();

        Assert.False(parent.CanRead);
        Assert.Throws<ObjectDisposedException>(() => parent.ReadByte());
    }

    /// <summary>Verifies invalid constructor arguments are rejected.</summary>
    [Fact]
    public void Ctor_InvalidArguments_Throw()
    {
        using MemoryStream parent = CreateParent();
        Assert.Throws<ArgumentNullException>(() => new BoundedSubStream(null!, 0, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSubStream(parent, -1, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSubStream(parent, 0, -1, false));
    }
}
