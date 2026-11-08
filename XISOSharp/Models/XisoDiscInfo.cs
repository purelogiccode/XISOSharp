using System.Diagnostics.CodeAnalysis;

namespace XISOSharp.Models;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// A byte range on a disc image, expressed in bytes and 2048-byte sectors.
/// </summary>
/// <param name="Name">Human-readable range name (for example <c>game</c> or <c>video L0</c>).</param>
/// <param name="ByteOffset">First byte of the range within the image file.</param>
/// <param name="ByteLength">Length of the range in bytes.</param>
/// <param name="StartSector">First 2048-byte sector of the range.</param>
/// <param name="SectorCount">Number of 2048-byte sectors in the range.</param>
/// <param name="Available">Whether the range can be read from this source (video fields are absent for containers).</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public inspection payload: consumers render every range field; the library only builds it.")]
public record DiscPartition(
    string Name,
    long ByteOffset,
    long ByteLength,
    long StartSector,
    long SectorCount,
    bool Available);

/// <summary>
/// A security-sector range read from an embedded Xbox security sector. Each range
/// is 4096 sectors (8 MiB) long.
/// </summary>
/// <param name="StartSector">First physical sector number stored in the security sector.</param>
/// <param name="EndSector">Last physical sector number stored in the security sector.</param>
public record DiscSecuritySectorRange(
    long StartSector,
    long EndSector)
{
    /// <summary>Number of sectors in the range (always 4096 for Xbox security ranges).</summary>
    public long SectorCount => EndSector - StartSector + 1;
}

/// <summary>
/// Physical Format Information (PFI) sector fields observed in a Redump image.
/// </summary>
/// <param name="ByteOffset">Byte offset of the PFI sector within the image.</param>
/// <param name="BookType">DVD book type (bits 7-4 of byte 0).</param>
/// <param name="PartVersion">Part version (bits 3-0 of byte 0).</param>
/// <param name="DiscSize">Disc size code (bits 7-4 of byte 1).</param>
/// <param name="MaximumRate">Maximum rate code (bits 3-0 of byte 1).</param>
/// <param name="LayerCount">Number of layers (bits 7-5 of byte 2).</param>
/// <param name="TrackPath">Track path (bit 4 of byte 2; 1 = opposite track path).</param>
/// <param name="LayerType">Layer type (bits 3-0 of byte 2).</param>
/// <param name="DataStartSector">Starting physical sector number of the data area.</param>
/// <param name="DataEndSector">Ending physical sector number of the data area.</param>
/// <param name="Layer0EndSector">End physical sector number of layer 0 (the layer break).</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public inspection payload: consumers render every PFI field; the library only builds it.")]
public record DiscPhysicalFormatInfo(
    long ByteOffset,
    int BookType,
    int PartVersion,
    int DiscSize,
    int MaximumRate,
    int LayerCount,
    int TrackPath,
    int LayerType,
    long DataStartSector,
    long DataEndSector,
    long Layer0EndSector);

/// <summary>
/// Redump/XGD disc-layout inspection result produced by
/// <see cref="XISOSharp.XisoDiscInfo"/>.
/// </summary>
/// <param name="Path">Image path as supplied by the caller.</param>
/// <param name="Format">Detected container format.</param>
/// <param name="IsContainer">
/// <c>true</c> for compressed or logical containers (CISO, CCI, split sets and
/// GOD); their video/PFI/security fields are not read because the container
/// exposes only the logical image view.
/// </param>
/// <param name="FileLength">Logical image length in bytes.</param>
/// <param name="TotalSectors">Logical image length in 2048-byte sectors.</param>
/// <param name="Volume">Probed XISO volume descriptor.</param>
/// <param name="DiscFormat">Friendly disc-layout name from <see cref="VolumeInfo.DiscFormat"/>.</param>
/// <param name="XgdType">XGD generation name (<c>XGD1</c>, <c>XGD2</c>, <c>XGD2 Hybrid</c>, <c>XGD3</c> or <c>RAW</c>).</param>
/// <param name="RedumpTypeIndex">Matched Redump ISO type index (0-8), or <c>null</c>.</param>
/// <param name="RedumpSizeMatch">Whether the image length matches a known Redump image size.</param>
/// <param name="RedumpTypeName">Human-readable Redump type name, or <c>null</c>.</param>
/// <param name="VideoTypeIndex">Video partition type index (0-18), or <c>null</c>.</param>
/// <param name="VideoTypeName">Human-readable video/wave name, or <c>null</c>.</param>
/// <param name="Wave">Detected XGD wave label, or <c>null</c> when the PVD timestamp is unknown/absent.</param>
/// <param name="VideoL0">Layer-0 head of the video partition, or <c>null</c>.</param>
/// <param name="VideoL1">Layer-1 tail of the video partition, or <c>null</c>.</param>
/// <param name="GamePartition">Game (XISO filesystem) partition range, or <c>null</c> when the image is invalid.</param>
/// <param name="Pfi">Observed PFI sector, or <c>null</c> when absent/unreadable.</param>
/// <param name="SecuritySectorsSource">Source of <paramref name="SecuritySectors"/> (<c>embedded</c>, <c>sectors.txt</c>) or <c>null</c>.</param>
/// <param name="SecuritySectors">Security-sector ranges found for this image.</param>
/// <param name="Warnings">Consistency warnings and actionable notes.</param>
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global",
    Justification = "Public inspection payload: consumers read the fields; the library itself only builds them.")]
public sealed record XisoDiscLayout(
    string Path,
    XisoImageFormat Format,
    bool IsContainer,
    long FileLength,
    long TotalSectors,
    VolumeInfo Volume,
    string DiscFormat,
    string XgdType,
    int? RedumpTypeIndex,
    bool RedumpSizeMatch,
    string? RedumpTypeName,
    int? VideoTypeIndex,
    string? VideoTypeName,
    string? Wave,
    DiscPartition? VideoL0,
    DiscPartition? VideoL1,
    DiscPartition? GamePartition,
    DiscPhysicalFormatInfo? Pfi,
    string? SecuritySectorsSource,
    IReadOnlyList<DiscSecuritySectorRange> SecuritySectors,
    IReadOnlyList<string> Warnings);
