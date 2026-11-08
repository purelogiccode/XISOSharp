using StfsSharp;
using StfsSharp.Models;
using XISOSharp.Interfaces;

namespace XISOSharp.BlockDevice;

/// <summary>
/// Internal <see cref="IBlockDevice"/> adapter over a Games-on-Demand
/// (GOD/SVOD) container. The public consumer surface is
/// <see cref="StfsSharp.GodVolume"/>; this type only lets the XISOSharp
/// detection layer treat a GOD container like every other sector source.
/// </summary>
internal sealed class GodVolumeBlockDevice : IBlockDevice
{
    private readonly GodVolume _volume;

    /// <summary>Opens a GOD container (header file or its <c>.data</c> directory).</summary>
    internal GodVolumeBlockDevice(string path) : this(path, FileShare.Read)
    {
    }

    /// <summary>Opens a GOD container (header file or its <c>.data</c> directory).</summary>
    internal GodVolumeBlockDevice(string path, FileShare share)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _volume = GodVolume.Open(path, share);
        Info = _volume.Info;
    }

    /// <summary>Parsed header and fragment metadata.</summary>
    internal GodVolumeInfo Info { get; }

    /// <summary>Full path of the GOD header file.</summary>
    internal string HeaderPath => Info.HeaderPath;

    /// <summary>Full path of the matching <c>.data</c> directory.</summary>
    internal string DataDirectory => Info.DataDirectory;

    /// <summary>Logical uncompressed image length in bytes.</summary>
    public long Length => _volume.Length;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer) => _volume.Read(offset, buffer);

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("GOD containers are read-only through this block device.");

    /// <inheritdoc/>
    public void Dispose() => _volume.Dispose();
}
