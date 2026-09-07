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
    /// Normalizes an <see cref="IFilesystem"/> path to a host path.
    /// </summary>
    /// <remarks>
    /// With no <see cref="Root"/> (the shared cwd-relative instance),
    /// fully-qualified host paths pass through untouched, so absolute
    /// destinations (e.g. the resume-cache probes in <c>CopyOutFile</c>) hit
    /// the intended file instead of a cwd-relative lookalike (BUG-LIB-020).
    /// With a <see cref="Root"/>, everything — including <c>/</c>-led
    /// image-internal paths — resolves underneath it, and anything escaping
    /// via a rooted second argument (<c>C:\evil</c> on Windows survives
    /// <c>TrimStart</c> and would discard the root in <c>Path.Combine</c>)
    /// or a <c>..</c> climb throws instead (BUG-LIB-019).
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">
    /// The path escapes <see cref="Root"/>.
    /// </exception>
    private string Resolve(string path)
    {
        if (Root is null)
        {
            // Cwd-relative legacy resolution (absolute paths included).
            return Path.GetFullPath(path);
        }

        string normalized = path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(Root, normalized));

        string rootFull = Path.GetFullPath(Root);
        if (!XisoPaths.AreSamePath(full, rootFull) && !XisoPaths.IsWithinDirectory(full, rootFull))
        {
            throw new UnauthorizedAccessException(
                $"Path '{path}' escapes the destination root '{Root}'.");
        }

        return full;
    }

    /// <inheritdoc/>
    public Stream CreateFile(string path) =>
        new FileStream(
            Resolve(path),
            new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 65536
            });

    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(Resolve(path));

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
