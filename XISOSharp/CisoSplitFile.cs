namespace XISOSharp;

/// <summary>
/// Split CSO naming and part discovery, mirroring <c>ciso::split</c> from the <c>ciso</c> crate 0.2
/// (xdvdfs 0.8.3). Part files are <c>&lt;base&gt;.1.cso</c>, <c>&lt;base&gt;.2.cso</c>, …; each part
/// receives its data at the <em>global</em> (absolute) stream position, so part <c>n</c> holds the
/// global byte range <c>[(n-1)·splitPoint, n·splitPoint)</c> and its leading gap reads as zeros —
/// the sparse-file layout the Rust <c>SplitOutput</c> produces.
/// </summary>
internal static class CisoSplitFile
{
    /// <summary>Number of characters in the <c>.1.cso</c> part suffix.</summary>
    private const int PartSuffixLength = 6;

    /// <summary>
    /// Builds the path of split part <paramref name="partIndex"/> (0-based) for an output path,
    /// mirroring Rust <c>Path::with_extension(format!("{n}.cso"))</c>:
    /// <c>game.cso</c> → <c>game.1.cso</c>, <c>game</c> → <c>game.1.cso</c>, <c>a.b.cso</c> → <c>a.b.1.cso</c>.
    /// </summary>
    public static string PartPath(string outputCsoPath, long partIndex)
        => Path.ChangeExtension(outputCsoPath, $"{partIndex + 1}.cso");

    /// <summary>
    /// Returns true when <paramref name="path"/> refers to the first part of a split CSO
    /// (<c>*.1.cso</c>), matching the detection in <c>xdvdfs-cli/src/img.rs::open_image</c>.
    /// </summary>
    public static bool IsSplitPath(string path)
        => path.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens every existing part for a split path such as <c>game.1.cso</c>.</summary>
    public static List<FileStream> OpenParts(string firstPartPath)
    {
        var baseName = firstPartPath[..^PartSuffixLength];
        var parts = new List<FileStream>();
        for (var i = 0;; i++)
        {
            var partPath = PartPath(baseName, i);
            if (!File.Exists(partPath)) break;
            parts.Add(new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536));
        }

        return parts;
    }
}