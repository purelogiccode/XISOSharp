namespace XISOSharp.BlockDevice;

/// <summary>
/// In-memory <see cref="IBlockDevice"/> for unit tests, mirroring <c>xdvdfs</c> <c>no_std</c>
/// <c>AsRef&lt;[u8]&gt;</c> impl and <c>Box&lt;dyn BlockDeviceRead&gt;</c>.
/// </summary>
public sealed class MemoryBlockDevice : IBlockDevice
{
    private byte[] _data;
    private long _length;

    /// <summary>Creates an empty device.</summary>
    public MemoryBlockDevice() => _data = [];

    /// <summary>Creates a device initialized with <paramref name="data"/> (copied).</summary>
    public MemoryBlockDevice(ReadOnlySpan<byte> data)
    {
        _data = data.ToArray();
        _length = _data.Length;
    }

    /// <summary>Creates a device with a fixed capacity (zero-filled).</summary>
    public MemoryBlockDevice(long capacity)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new byte[capacity];
        _length = capacity;
    }

    /// <inheritdoc/>
    public long Length => _length;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= _length) return 0;
        int available = (int)Math.Min(buffer.Length, _length - offset);
        _data.AsSpan((int)offset, available).CopyTo(buffer);
        // Zero-fill remainder if reading beyond written length but within buffer
        if (available < buffer.Length)
            buffer[available..].Clear();
        return available;
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        long end = offset + buffer.Length;
        EnsureCapacity(end);
        buffer.CopyTo(_data.AsSpan((int)offset, buffer.Length));
        if (end > _length) _length = end;
    }

    private void EnsureCapacity(long needed)
    {
        if (needed <= _data.Length) return;
        long newSize = Math.Max(needed, _data.Length == 0 ? 4096 : _data.Length * 2);
        while (newSize < needed) newSize *= 2;
        Array.Resize(ref _data, (int)newSize);
    }

    /// <summary>Returns a copy of the written bytes.</summary>
    public byte[] ToArray()
    {
        var outArr = new byte[_length];
        Array.Copy(_data, outArr, _length);
        return outArr;
    }

    /// <summary>Returns a span over the written bytes (read-only).</summary>
    public ReadOnlySpan<byte> AsSpan() => _data.AsSpan(0, (int)_length);

    /// <inheritdoc/>
    public void Dispose() { }
}
