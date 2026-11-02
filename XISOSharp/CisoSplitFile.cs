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
    internal static string PartPath(string outputCsoPath, long partIndex) =>
        Path.ChangeExtension(outputCsoPath, $"{partIndex + 1}.cso");

    /// <summary>
    /// Returns true when <paramref name="path"/> refers to the first part of a split CSO
    /// (<c>*.1.cso</c>), matching the detection in <c>xdvdfs-cli/src/img.rs::open_image</c>.
    /// </summary>
    internal static bool IsSplitPath(string path) => path.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens every existing part for a split path such as <c>game.1.cso</c>.</summary>
    /// <param name="firstPartPath">Path of the first part (<c>*.1.cso</c>).</param>
    /// <param name="share">File sharing mode for the part files.</param>
    internal static List<FileStream> OpenParts(string firstPartPath, FileShare share = FileShare.Read)
    {
        List<FileStream> parts = new();
        foreach (string partPath in DiscoverPartPaths(firstPartPath))
        {
            parts.Add(new FileStream(partPath, FileMode.Open, FileAccess.Read, share, 65536));
        }

        return parts;
    }

    /// <summary>
    /// Enumerates existing split part paths for a <c>*.1.cso</c> path. A
    /// non-split path resolves to itself when it exists.
    /// </summary>
    internal static List<string> DiscoverPartPaths(string firstPartPath)
    {
        List<string> parts = new();
        if (!IsSplitPath(firstPartPath))
        {
            if (File.Exists(firstPartPath)) parts.Add(firstPartPath);
            return parts;
        }

        string baseName = firstPartPath[..^PartSuffixLength];
        for (int i = 1; ; i++)
        {
            // Append rather than ChangeExtension: a dotted base (`My.Game.1.cso`
            // -> `My.Game`) must yield `My.Game.2.cso`, not `My.2.cso`.
            string partPath = $"{baseName}.{i}.cso";
            if (!File.Exists(partPath)) break;
            parts.Add(partPath);
        }

        ThrowIfPartGap(baseName, parts.Count);
        return parts;
    }

    private static void ThrowIfPartGap(string baseName, int contiguousCount)
    {
        string directory = Path.GetDirectoryName(baseName) ?? string.Empty;
        if (directory.Length == 0) directory = ".";
        string stem = Path.GetFileName(baseName);
        if (stem.Length == 0) return;

        string prefix = stem + ".";
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.cso"))
            {
                string name = Path.GetFileName(candidate);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".cso", StringComparison.OrdinalIgnoreCase)) continue;

                string digits = name[prefix.Length..^".cso".Length];
                if (!int.TryParse(digits, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int index))
                {
                    continue;
                }

                if (index > contiguousCount)
                {
                    throw new InvalidDataException(
                        $"Split CISO part {index} exists but part {contiguousCount + 1} is missing: {candidate}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The first part is missing; the caller reports not-found.
        }
    }
}
