using XISOSharp.Interfaces;
using XISOSharp.Models;
using SharpGodVolume = StfsSharp.GodVolume;

namespace XISOSharp.BlockDevice;

/// <summary>
/// <see cref="IBlockDevice"/> over a Games-on-Demand (GOD/SVOD) container:
/// a plaintext <c>XContent</c> header file beside a <c>&lt;header&gt;.data</c>
/// directory of <c>DataNNNN</c> fragments.
/// </summary>
/// <remarks>
/// <para>
/// The SVOD layout interleaves 0x1000-byte hash tables with the sector data:
/// every group of <c>0x198</c> sectors is preceded by a level-0 hash table,
/// and every group of <c>0xA1C4</c> level-0 tables is preceded by a level-1
/// table. Each fragment holds <c>0x14388</c> sectors; a fragment that would
/// cross the <c>0xA290000</c>-byte boundary rolls into the next one. This
/// device exposes only the sector data, not the hash tables.
/// </para>
/// <para>
/// This is a compatibility adapter over the standalone
/// <see href="https://www.nuget.org/packages/StfsSharp">StfsSharp</see>
/// <c>GodVolume</c> reader. Reads are serialized by the volume, so one
/// instance can be shared by concurrent readers. The device is read-only;
/// <see cref="Write"/> throws <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class GodBlockDevice : IBlockDevice
{
    private readonly SharpGodVolume _volume;

    /// <summary>Opens a GOD container (header file or its <c>.data</c> directory).</summary>
    /// <param name="path">GOD header path or the matching <c>.data</c> directory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The header or a data fragment was not found.</exception>
    /// <exception cref="InvalidDataException">The header is not a valid SVOD volume.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be opened.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public GodBlockDevice(string path) : this(path, FileShare.Read)
    {
    }

    /// <summary>
    /// Opens a GOD container with a caller-chosen share mode for the header and
    /// fragment files.
    /// </summary>
    /// <param name="path">GOD header path or the matching <c>.data</c> directory.</param>
    /// <param name="share">File sharing mode passed to every file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The header or a data fragment was not found.</exception>
    /// <exception cref="InvalidDataException">The header is not a valid SVOD volume.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be opened.</exception>
    /// <exception cref="IOException">Another I/O error occurred.</exception>
    public GodBlockDevice(string path, FileShare share)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _volume = SharpGodVolume.Open(path, share);
        Info = GodHeader.Map(_volume.Info);
    }

    /// <summary>Parsed header and fragment metadata.</summary>
    public GodVolumeInfo Info { get; }

    /// <summary>Full path of the GOD header file.</summary>
    public string HeaderPath => Info.HeaderPath;

    /// <summary>Full path of the matching <c>.data</c> directory.</summary>
    public string DataDirectory => Info.DataDirectory;

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
