namespace XISOSharp.BlockDevice;

/// <summary>
/// Abstraction over a random-access block device, mirroring <c>xdvdfs-core/src/blockdev.rs</c>
/// <c>BlockDeviceRead</c> / <c>BlockDeviceWrite</c> and <c>OffsetWrapper</c>.
/// Enables <c>FileBlockDevice</c>, <c>MemoryBlockDevice</c> (tests) and <c>CisoBlockDevice</c>
/// (CISO random access) to share the same <c>XisoReader</c> / <c>VerifyXiso</c> paths.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Length of the device in bytes.</summary>
    long Length { get; }

    /// <summary>Reads <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
    /// <returns>Number of bytes read.</returns>
    int Read(long offset, Span<byte> buffer);

    /// <summary>Writes <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
    void Write(long offset, ReadOnlySpan<byte> buffer);
}
