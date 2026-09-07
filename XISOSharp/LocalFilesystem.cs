using XISOSharp.Interfaces;

namespace XISOSharp;

/// <summary>
/// <see cref="IFilesystem"/> over the local disk: the default extraction
/// destination. Paths resolve against <paramref name="root"/> (or the process
/// current directory when no root is given, matching the legacy chdir-based
/// unpack). File creation uses the same options as the reader's direct
/// extraction path (<c>FileMode.Create</c>, 64 KiB buffered, no sharing).
/// </summary>
/// <param name="root">
/// Destination root directory, or <c>null</c> to resolve relative paths
/// against the process current directory at call time.
/// </param>
public sealed class LocalFilesystem(string? root = null) : IFilesystem
{
    /// <summary>
    /// Shared cwd-relative instance (root <c>null</c>): the destination used by
    /// every legacy unpack/extract path.
    /// </summary>
    public static LocalFilesystem Instance { get; } = new();

    /// <summary>Destination root directory, or <c>null</c> for cwd-relative paths.</summary>
    public string? Root { get; } = root;

    /// <summary>
    /// Normalizes an <see cref="IFilesystem"/> path (forward slashes, root
    /// relative) to a host path under <see cref="Root"/>.
    /// </summary>
    private string Resolve(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        return Root is null ? normalized : Path.Combine(Root, normalized);
    }

    /// <inheritdoc/>
    public Stream CreateFile(string path)
    {
        return new FileStream(
            Resolve(path),
            new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536
            });
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(Resolve(path));
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(Resolve(path));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public long FileLength(string path)
    {
        try
        {
            return new FileInfo(Resolve(path)).Length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }
}