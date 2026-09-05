namespace ZARSharp.Pipeline;

/// <summary>
/// Per-stage progress weights, ported from <c>core.py::_get_step_weights</c>
/// (ZarManager 1.2.0). Non-<see cref="ZarProcessMode.Auto"/> runs are one
/// stage (<c>(0, 1)</c>); <c>Auto</c> splits an item's 0..1 across the
/// 7z / XISO-extract / ZAR stages by input kind so a batch bar stays linear.
/// </summary>
public static class ZarStageWeights
{
    /// <summary>Archive-extraction stage (7z and friends).</summary>
    public const string Archive = "7z";

    /// <summary>XISO-extraction stage.</summary>
    public const string Xiso = "xiso";

    /// <summary>ZAR-compression stage.</summary>
    public const string Zar = "zar";

    /// <summary>Single-stage runs.</summary>
    public const string All = "all";

    /// <summary>One weighted segment: <c>Base</c> offset plus <c>Length</c> times stage-local 0..1.</summary>
    public sealed record Segment(string Stage, double Base, double Length);

    /// <summary>Weight segments for <paramref name="fileOrDir"/> under <paramref name="mode"/>.</summary>
    public static IReadOnlyList<Segment> ForFile(string fileOrDir, ZarProcessMode mode)
    {
        if (mode != ZarProcessMode.Auto)
        {
            return [new Segment(All, 0.0, 1.0)];
        }

        if (Directory.Exists(fileOrDir))
        {
            return [new Segment(Zar, 0.0, 1.0)];
        }

        var suffix = Path.GetExtension(fileOrDir).ToLowerInvariant();
        if (ProcessableFiles.ArchiveExtensions.Contains(suffix))
        {
            return
            [
                new Segment(Archive, 0.0, 0.33),
                new Segment(Xiso, 0.33, 0.33),
                new Segment(Zar, 0.66, 0.34),
            ];
        }

        if (ProcessableFiles.IsoExtensions.Contains(suffix))
        {
            return
            [
                new Segment(Xiso, 0.0, 0.5),
                new Segment(Zar, 0.5, 0.5),
            ];
        }

        return [new Segment(Zar, 0.0, 1.0)];
    }

    /// <summary>Maps a stage-local 0..1 fraction to the item-global 0..1.</summary>
    public static double Rebase(IReadOnlyList<Segment> segments, string stage, double local) =>
        segments.FirstOrDefault(s => string.Equals(s.Stage, stage, StringComparison.Ordinal)) is { } seg
            ? seg.Base + (seg.Length * local)
            : local;
}
