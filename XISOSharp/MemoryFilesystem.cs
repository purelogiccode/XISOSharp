using System.Collections.ObjectModel;
using XISOSharp.Interfaces;

namespace XISOSharp;

/// <summary>
/// <see cref="IFilesystem"/> and <see cref="ISourceFilesystem"/> backed by
/// process memory: extraction or packing without touching the disk (xdvdfs
/// #166, TODO #7 - the web/wasm unpacker's destination, and the library/GUI
/// route for previewing or piping files).
/// Files are committed byte snapshots: each <see cref="CreateFile(string)"/> stream
/// buffers in memory and publishes its bytes on dispose, so completed files
/// are visible to <see cref="FileExists"/>, <see cref="FileLength"/>,
/// <see cref="ReadAllBytes"/>, and <see cref="FileNames"/> immediately after the
/// unpack writes them. Re-creating a file replaces it (truncates). Parent
/// directories of a created file are materialized automatically. Not
/// thread-safe; unpacking writes and packing reads sequentially.
/// </summary>
public sealed class MemoryFilesystem : IFilesystem, ISourceFilesystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A <see cref="MemoryStream"/> whose bytes are published to the owning
    /// filesystem when disposed (the unpack closes each file on completion).
    /// </summary>
    private sealed class SinkStream(MemoryFilesystem owner, string key, int capacity) : MemoryStream(capacity)
    {
        private readonly MemoryFilesystem _owner = owner;
        private readonly string _key = key;

        /// <inheritdoc/>
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
    public Stream CreateFile(string path) => CreateFile(path, null);

    /// <inheritdoc/>
    /// <remarks>
    /// A known length preallocates the in-memory buffer (PlanToEnhance Phase 7);
    /// lengths beyond <see cref="int.MaxValue"/> fall back to a growable stream.
    /// </remarks>
    public Stream CreateFile(string path, long? expectedLength)
    {
        string key = Normalize(path);
        if (key.Length == 0)
        {
            throw new ArgumentException("Destination root cannot hold a file.", nameof(path));
        }

        MaterializeParents(key);
        int capacity = expectedLength is > 0 and <= int.MaxValue ? (int)expectedLength.Value : 0;
        return new SinkStream(this, key, capacity);
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
    public long FileLength(string path) =>
        _files.TryGetValue(Normalize(path), out byte[]? bytes) ? bytes.LongLength : -1;

    /// <inheritdoc/>
    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        string key = Normalize(path);
        return key.Length == 0 || _directories.Contains(key);
    }

    /// <inheritdoc/>
    public IReadOnlyList<SourceEntry> EnumerateEntries(string path)
    {
        string key = Normalize(path);
        if (!DirectoryExists(key))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {path}");
        }

        Dictionary<string, SourceEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string filePath, byte[] bytes) in _files)
        {
            SplitEntry(filePath, key, out string parent, out string name);
            if (name.Length > 0 && string.Equals(parent, key, StringComparison.OrdinalIgnoreCase))
            {
                entries[name] = new SourceEntry(name, IsDirectory: false, bytes.LongLength);
            }
        }

        foreach (string directory in _directories)
        {
            SplitEntry(directory, key, out string parent, out string name);
            if (name.Length > 0 && string.Equals(parent, key, StringComparison.OrdinalIgnoreCase))
            {
                // A directory wins over a same-named file; the writer rejects
                // case-insensitive collisions on its own.
                entries[name] = new SourceEntry(name, IsDirectory: true, Length: 0);
            }
        }

        return [.. entries.Values];
    }

    /// <inheritdoc/>
    public Stream OpenRead(string path)
    {
        string key = Normalize(path);
        if (!_files.TryGetValue(key, out byte[]? bytes))
        {
            throw new FileNotFoundException($"Source file not found: {path}", path);
        }

        // The committed array is replaced (not mutated) when the path is
        // re-created, so this read-only view is a stable snapshot.
        return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
    }

    /// <summary>
    /// Splits a root-relative entry path into its parent and leaf relative to
    /// <paramref name="current"/> (which is empty for the root).
    /// </summary>
    private static void SplitEntry(string entry, string current, out string parent, out string name)
    {
        string normalized = Normalize(entry);
        string prefix = current.Length == 0 ? string.Empty : current + "/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            parent = string.Empty;
            name = string.Empty;
            return;
        }

        string remainder = normalized[prefix.Length..];
        int slash = remainder.IndexOf('/', StringComparison.Ordinal);
        parent = current;
        name = slash < 0 ? remainder : remainder[..slash];
    }

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
