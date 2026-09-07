using System.Collections.ObjectModel;
using XISOSharp.Interfaces;

namespace XISOSharp;

/// <summary>
/// <see cref="IFilesystem"/> backed by process memory: extraction without
/// touching the disk (xdvdfs #166, TODO #7 — the web/wasm unpacker's
/// destination, and the library/GUI route for previewing or piping files).
/// Files are committed byte snapshots: each <see cref="CreateFile"/> stream
/// buffers in memory and publishes its bytes on dispose, so completed files
/// are visible to <see cref="FileExists"/>, <see cref="FileLength"/>,
/// <see cref="ReadAllBytes"/>, and <see cref="FileNames"/> immediately after the
/// unpack writes them. Re-creating a file replaces it (truncates). Parent
/// directories of a created file are materialized automatically. Not
/// thread-safe; unpacking writes sequentially.
/// </summary>
public sealed class MemoryFilesystem : IFilesystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A <see cref="MemoryStream"/> whose bytes are published to the owning
    /// filesystem when disposed (the unpack closes each file on completion).
    /// </summary>
    private sealed class SinkStream(MemoryFilesystem owner, string key) : MemoryStream
    {
        private readonly MemoryFilesystem _owner = owner;
        private readonly string _key = key;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner._files[_key] = ToArray();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Normalizes a destination path to the dictionary key form (no slashes at either end).</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>Materializes every parent directory of <paramref name="key"/>.</summary>
    private void MaterializeParents(string key)
    {
        int index = -1;
        while ((index = key.IndexOf('/', index + 1)) > 0)
        {
            _directories.Add(key[..index]);
        }
    }

    /// <inheritdoc/>
    public Stream CreateFile(string path)
    {
        string key = Normalize(path);
        if (key.Length == 0)
        {
            throw new ArgumentException("Destination root cannot hold a file.", nameof(path));
        }

        MaterializeParents(key);
        return new SinkStream(this, key);
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        string key = Normalize(path);
        if (key.Length == 0)
        {
            return;
        }

        MaterializeParents(key);
        _directories.Add(key);
    }

    /// <inheritdoc/>
    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    /// <inheritdoc/>
    public long FileLength(string path) => _files.TryGetValue(Normalize(path), out byte[]? bytes) ? bytes.LongLength : -1;

    /// <summary>
    /// Returns a snapshot of the bytes stored at <paramref name="path"/>
    /// (committed when the unpack closed the file's stream).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when no file exists at <paramref name="path"/>.</exception>
    public byte[] ReadAllBytes(string path) => _files[Normalize(path)];

    /// <summary>Snapshot of stored file paths (destination-root-relative, forward slashes).</summary>
    public ReadOnlyCollection<string> FileNames => new([.. _files.Keys]);

    /// <summary>Snapshot of created directory paths (destination-root-relative, forward slashes).</summary>
    public ReadOnlyCollection<string> DirectoryNames => new([.. _directories]);
}
