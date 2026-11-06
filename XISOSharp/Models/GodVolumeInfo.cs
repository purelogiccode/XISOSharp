using System.Diagnostics.CodeAnalysis;

namespace XISOSharp.Models;

/// <summary>
/// Describes the sector mapping of a Games-on-Demand (GOD/SVOD) container.
/// A GOD container is a plaintext <c>XContent</c> header file beside a
/// <c>&lt;header&gt;.data</c> directory that holds one or more <c>DataNNNN</c>
/// fragment files.
/// </summary>
/// <param name="HeaderPath">Full path of the GOD header file.</param>
/// <param name="DataDirectory">Full path of the matching <c>.data</c> directory.</param>
/// <param name="DataFileCount">Number of <c>DataNNNN</c> fragment files.</param>
/// <param name="StartingBlock">SVOD start data block (in 0x1000-byte blocks).</param>
/// <param name="DataBlockCount">SVOD data block count (in 0x1000-byte blocks).</param>
/// <param name="EnhancedGdf">True when the enhanced-GDF layout bit is set.</param>
/// <param name="Length">Logical uncompressed image length in bytes.</param>
/// <param name="Parts">
/// Header path followed by the fragment files in image order. The first element
/// is the header, not a sector-bearing part.
/// </param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public SVOD mapping: fragment/block counts are diagnostic metadata for callers.")]
public sealed record GodVolumeInfo(
    string HeaderPath,
    string DataDirectory,
    uint DataFileCount,
    uint StartingBlock,
    uint DataBlockCount,
    bool EnhancedGdf,
    long Length,
    IReadOnlyList<string> Parts);
