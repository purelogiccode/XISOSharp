using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// In-memory <see cref="IBlockDevice"/> for unit tests, mirroring <c>xdvdfs</c> <c>no_std</c>
/// <c>AsRef&lt;[u8]&gt;</c> impl and <c>Box&lt;dyn BlockDeviceRead&gt;</c>.
/// </summary>
public sealed class MemoryBlockDevice : IBlockDevice
{
    private byte[] _data;

    /// <summary>Creates an empty device.</summary>
    public MemoryBlockDevice()
    {
        _data = [];
    }

    /// <summary>Creates a device initialized with <paramref name="data"/> (copied).</summary>
    public MemoryBlockDevice(ReadOnlySpan<byte> data)
    {
        _data = data.ToArray();
        Length = _data.Length;
    }

    /// <summary>Creates a device with a fixed capacity (zero-filled).</summary>
    public MemoryBlockDevice(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _data = new byte[capacity];
        Length = capacity;
    }

    /// <inheritdoc/>
    public long Length { get; private set; }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length) return 0;
        var available = (int)Math.Min(buffer.Length, Length - offset);
        _data.AsSpan((int)offset, available).CopyTo(buffer);
        // Zero-fill remainder if reading beyond written length but within buffer
        if (available < buffer.Length)
            buffer[available..].Clear();
        return available;
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var end = offset + buffer.Length;
        EnsureCapacity(end);
        buffer.CopyTo(_data.AsSpan((int)offset, buffer.Length));
        if (end > Length) Length = end;
    }

    private void EnsureCapacity(long needed)
    {
        if (needed <= _data.Length) return;
        var newSize = Math.Max(needed, _data.Length == 0 ? 4096 : _data.Length * 2);
        while (newSize < needed) newSize *= 2;
        Array.Resize(ref _data, (int)newSize);
    }

    /// <summary>Returns a copy of the written bytes.</summary>
    public byte[] ToArray()
    {
        var outArr = new byte[Length];
        Array.Copy(_data, outArr, Length);
        return outArr;
    }

    /// <summary>Returns a span over the written bytes (read-only).</summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        return _data.AsSpan(0, (int)Length);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}