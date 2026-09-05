namespace ZARSharp.Seekable;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Controls when <see cref="SeekableWriter"/> starts a new frame. Port of
/// zeekstd's <c>FrameSizePolicy</c>: <see cref="Uncompressed"/> cuts frames
/// at an uncompressed size (exact), <see cref="Compressed"/> cuts once the
/// frame's compressed size reaches the threshold (checked at 128 KiB input
/// granularity, like the oracle's CLI reads). Either way a new frame always
/// starts at 1 GiB of uncompressed data (<c>SEEKABLE_MAX_FRAME_SIZE</c>).
/// </summary>
public enum SeekableFrameSizePolicy
{
    /// <summary>Start a new frame at an uncompressed size (the default).</summary>
    Uncompressed = 0,

    /// <summary>Start a new frame at a compressed size.</summary>
    Compressed = 1,
}

/// <summary>
/// Options for <see cref="SeekableWriter"/>. Defaults mirror the
/// <c>zeekstd</c> CLI (level 3, 2 MiB uncompressed frames, frame checksums
/// on) so default-against-default output is byte-identical.
/// </summary>
public sealed class SeekableOptions
{
    /// <summary>Maximum uncompressed data per frame (1 GiB).</summary>
    public const int MaxFrameSize = 0x40000000;

    /// <summary>zstd compression level 1..22 (default 3).</summary>
    public int Level { get; init; } = 3;

    /// <summary>
    /// Frame size threshold in bytes for <see cref="Policy"/> (default 2 MiB,
    /// like the oracle). Capped at <see cref="MaxFrameSize"/>.
    /// </summary>
    public int FrameSize { get; init; } = 2 * 1024 * 1024;

    /// <summary>Whether <see cref="FrameSize"/> applies to compressed or uncompressed size.</summary>
    public SeekableFrameSizePolicy Policy { get; init; } = SeekableFrameSizePolicy.Uncompressed;

    /// <summary>Write a 4-byte content checksum per frame (default true, like the oracle CLI).</summary>
    public bool Checksum { get; init; } = true;
}
