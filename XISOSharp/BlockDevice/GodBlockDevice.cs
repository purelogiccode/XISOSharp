using XISOSharp.Interfaces;
using XISOSharp.Models;

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
/// Reads are serialized on an internal lock, so one instance can be shared by
/// concurrent readers. The device is read-only; <see cref="Write"/> throws
/// <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class GodBlockDevice : IBlockDevice
{
    private const int SectorSize = Constants.SectorSize;
    private const long SectorsPerDataFile = 0x14388;
    private const long SectorsPerHashBlock = 0x198;
    private const long HashBlockSize = 0x1000;
    private const long DataAreaOffset = 0x2000;
    private const long MaxFragmentSize = 0xA290000;
    private const long SecuritySector = 0x20;
    private const long SecuritySectorOffsetNormal = 0x12000;
    private const long SecuritySectorOffsetEnhanced = 0x2000;

#pragma warning disable MA0158 // object keeps net8 compat (System.Threading.Lock is net9+)
    private readonly object _sync = new();
#pragma warning restore MA0158
    private readonly FileStream[] _fragments;
    private bool _disposed;

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

        if (!GodHeader.TryReadVolume(path, share, out GodVolumeInfo? info))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException($"GOD container not found: {path}", path);

            throw new InvalidDataException($"Not a valid GOD/SVOD container: {path}");
        }

        Info = info;
        List<FileStream> fragments = new(info.Parts.Count - 1);
        try
        {
            for (int index = 0; index < info.Parts.Count - 1; index++)
            {
                fragments.Add(new FileStream(
                    info.Parts[index + 1],
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = share,
                        BufferSize = 1 << 16,
                        Options = FileOptions.RandomAccess,
                    }));
            }
        }
        catch
        {
            foreach (FileStream fragment in fragments)
            {
                fragment.Dispose();
            }

            throw;
        }

        _fragments = fragments.ToArray();
    }

    /// <summary>Parsed header and fragment metadata.</summary>
    public GodVolumeInfo Info { get; }

    /// <summary>Full path of the GOD header file.</summary>
    public string HeaderPath => Info.HeaderPath;

    /// <summary>Full path of the matching <c>.data</c> directory.</summary>
    public string DataDirectory => Info.DataDirectory;

    /// <summary>Logical uncompressed image length in bytes.</summary>
    public long Length => Info.Length;

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty) return 0;

            long length = Info.Length;
            if (offset >= length) return 0;

            int toRead = (int)Math.Min(buffer.Length, length - offset);
            int total = 0;
            long position = offset;
            while (total < toRead)
            {
                long sector = position / SectorSize;
                int offsetInSector = (int)(position % SectorSize);
                int chunk = Math.Min(toRead - total, SectorSize - offsetInSector);

                if (IsZeroFilled(sector))
                {
                    buffer.Slice(total, chunk).Clear();
                }
                else
                {
                    MapSector(sector, out int fragmentIndex, out long fragmentOffset);
                    FileStream fragment = _fragments[fragmentIndex];
                    fragment.Seek(fragmentOffset + offsetInSector, SeekOrigin.Begin);
                    int read = fragment.Read(buffer.Slice(total, chunk));
                    if (read != chunk)
                    {
                        throw new InvalidDataException(
                            $"GOD data fragment is truncated at sector {sector} " +
                            $"(fragment {fragmentIndex}, offset {fragmentOffset + offsetInSector}).");
                    }
                }

                total += chunk;
                position += chunk;
            }

            return total;
        }
    }

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("GOD containers are read-only through this block device.");

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (FileStream fragment in _fragments)
            {
                fragment.Dispose();
            }
        }
    }

    private bool IsZeroFilled(long sector)
    {
        if (sector is SecuritySector or SecuritySector + 1)
            return false;

        return sector < (long)Info.StartingBlock * 2;
    }

    private void MapSector(long sector, out int fragmentIndex, out long fragmentOffset)
    {
        if (sector is SecuritySector or SecuritySector + 1)
        {
            fragmentIndex = 0;
            fragmentOffset = (Info.EnhancedGdf
                ? SecuritySectorOffsetEnhanced
                : SecuritySectorOffsetNormal) + (sector - SecuritySector) * SectorSize;
            return;
        }

        long trueBlock = sector - (long)Info.StartingBlock * 2;
        if (Info.EnhancedGdf)
            trueBlock += 2;

        long fileIndex = trueBlock / SectorsPerDataFile;
        long fileBlock = trueBlock % SectorsPerDataFile;
        long level0Tables = (fileBlock / SectorsPerHashBlock) + 1;
        long level1Tables = (level0Tables / 0xA1C4) + 1;
        long offset = fileBlock * SectorSize + (level0Tables + level1Tables) * HashBlockSize;

        // A fragment that would cross the 0xA290000-byte boundary rolls into
        // the next fragment; the first two hash tables of the new fragment are
        // skipped by the +0x2000 rule (xenia BlockToOffsetSVOD).
        if (offset >= MaxFragmentSize)
        {
            fileIndex++;
            offset = (offset % MaxFragmentSize) + DataAreaOffset;
        }

        if (fileIndex >= _fragments.Length)
        {
            throw new InvalidDataException(
                $"GOD container references data fragment {fileIndex}, but only {_fragments.Length} exist.");
        }

        fragmentIndex = (int)fileIndex;
        fragmentOffset = offset;
    }
}
