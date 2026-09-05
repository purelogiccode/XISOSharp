using ZARSharp.Zstd;

namespace ZARSharp.Pipeline;

/// <summary>
/// Options for <see cref="ZarPipeline"/> pack/extract work. Defaults mirror
/// the two upstreams: zstd level 6 with no checksum (like
/// <c>zarchive.exe</c> / <c>ZArchiveTool</c>, keeping byte-identical output)
/// and 4 workers (like ZarManager's <c>settings.json</c> default).
/// </summary>
public sealed class ZarPipelineOptions
{
    /// <summary>zstd level 1..22 for packing (default 6). Ignored for extract.</summary>
    public int Level { get; set; } = 6;

    /// <summary>Write per-block content checksums (default false, upstream parity).</summary>
    public bool Checksum { get; set; }

    /// <summary>
    /// Explicit block compressor, or null to build one from
    /// <see cref="Level"/>/<see cref="Checksum"/>. Pass
    /// <c>new ZarRawCompressor()</c> to store blocks raw.
    /// </summary>
    public IZarBlockCompressor? Compressor { get; set; }

    /// <summary>
    /// Sort directory entries ordinally before packing (default true).
    /// This is <see cref="ZArchiveTool"/>'s reproducible mode; false keeps
    /// native enumeration order like <c>recursive_directory_iterator</c>.
    /// </summary>
    public bool DeterministicOrder { get; set; } = true;

    /// <summary>What to do when the output path already exists (default Fail).</summary>
    public ZarCollisionPolicy CollisionPolicy { get; set; } = ZarCollisionPolicy.Fail;

    /// <summary>
    /// Batch parallelism (default 4, ZarManager's default). Clamped to >= 1;
    /// the actual worker count is <c>min(workers, items)</c> like
    /// <c>core.py</c>'s <c>ThreadPoolExecutor</c> sizing.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Delete a pack source directory after a successful pack (default false).
    /// Mirrors ZarManager's <c>keep_originals == false</c>; off by default
    /// because a library must not destroy inputs unless asked.
    /// </summary>
    public bool DeleteSourceOnSuccess { get; set; }

    /// <summary>Pause gate checked alongside the cancellation token.</summary>
    public PauseToken Pause { get; set; }

    internal IZarBlockCompressor ResolveCompressor() =>
        Compressor ?? new ZstdCompressor(new ZstdCompressionOptions { Level = Level, ChecksumFlag = Checksum });

    internal int ClampedWorkers(int items) =>
        Math.Min(Math.Max(1, MaxDegreeOfParallelism), Math.Max(1, items));
}
