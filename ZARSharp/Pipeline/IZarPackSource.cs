namespace ZARSharp.Pipeline;

/// <summary>
/// A packable tree. The engine pre-scans <see cref="Collect"/> output for
/// file/byte totals, then streams each file once, in order.
/// </summary>
public interface IZarPackSource
{
    /// <summary>Display path of the source (directory, image, ...).</summary>
    string DisplayPath { get; }

    /// <summary>
    /// Collects entries in pack order: directories before their children
    /// (each <c>MakeDir(recursive: false)</c> is valid when reached).
    /// </summary>
    IReadOnlyList<ZarPackEntry> Collect(CancellationToken cancellationToken = default);
}
