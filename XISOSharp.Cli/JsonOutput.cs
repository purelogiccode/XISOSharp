using System.Text.Json.Serialization;

namespace XISOSharp.Cli;

#pragma warning disable MA0048 // Related JSON DTOs are grouped intentionally.

/// <summary>
/// Source-generated JSON DTOs for <c>--json</c> (PlanToEnhance Phase 6). The
/// schema is intentionally flat and versioned by <c>schemaVersion</c>; command
/// output always writes JSON to stdout while human progress stays on stderr.
/// </summary>
internal sealed record JsonVersion(
    int SchemaVersion,
    string Command,
    string XisoSharp,
    string CciSharp,
    string ZArchiveSharp,
    string Runtime,
    string Os,
    string Architecture,
    string? Commit);

/// <summary>Volume summary shared by <c>info</c> and <c>sector-layout</c>.</summary>
internal sealed record JsonVolume(
    bool IsValid,
    string Format,
    long FileLength,
    long TotalSectors,
    long DiscOffset,
    int DescriptorSector,
    uint RootDirSector,
    uint RootDirSize,
    string? FileTime,
    ulong FileTimeRaw);

/// <summary>One directory entry (recursive list/tree output).</summary>
internal sealed record JsonEntry(
    string Path,
    string Name,
    bool IsDirectory,
    uint StartSector,
    long Size,
    byte Attributes);

/// <summary>One direct-child entry in <c>info</c> output.</summary>
internal sealed record JsonInfoEntry(
    string Name,
    bool IsDirectory,
    uint StartSector,
    long Size,
    byte Attributes,
    int LeftChildOffset,
    int RightChildOffset);

/// <summary><c>info</c> command payload.</summary>
internal sealed record JsonInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string Path,
    JsonVolume Volume,
    List<JsonInfoEntry> Entries);

/// <summary>One image's recursive listing.</summary>
internal sealed record JsonImageListing(
    string Source,
    int FileCount,
    long TotalBytes,
    List<JsonEntry> Entries);

/// <summary><c>list</c>/<c>tree</c> command payload.</summary>
internal sealed record JsonListing(
    int SchemaVersion,
    string Command,
    List<JsonImageListing> Images);

/// <summary>One checksum result.</summary>
internal sealed record JsonChecksum(
    string Path,
    string Algorithm,
    string Hash);

/// <summary><c>checksum</c> command payload (always an array, even for one input).</summary>
internal sealed record JsonChecksums(
    int SchemaVersion,
    string Command,
    List<JsonChecksum> Checksums);

/// <summary>One extent in a sector layout.</summary>
internal sealed record JsonExtent(
    string Path,
    bool IsDirectory,
    uint StartSector,
    uint SectorCount,
    long Size);

/// <summary>One contiguous sector range.</summary>
internal sealed record JsonRange(
    uint StartSector,
    uint SectorCount);

/// <summary><c>sector-layout</c> command payload.</summary>
internal sealed record JsonSectorLayout(
    int SchemaVersion,
    string Command,
    string Source,
    JsonVolume Volume,
    int FileCount,
    int DirCount,
    List<JsonExtent> Entries,
    List<JsonRange> UsedRanges,
    List<JsonRange> FreeRanges);

/// <summary><c>ranges</c> command payload.</summary>
internal sealed record JsonRanges(
    int SchemaVersion,
    string Command,
    string Source,
    List<JsonRange> System,
    List<JsonRange> Files);

/// <summary><c>filetime</c> command payload.</summary>
internal sealed record JsonFileTime(
    int SchemaVersion,
    string Command,
    string Source,
    string Iso8601,
    ulong Raw,
    string Hex);

/// <summary><c>xex-info</c> command payload.</summary>
internal sealed record JsonXexInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string Path,
    uint ModuleFlags,
    uint HeaderSize,
    uint EntryPoint,
    uint ImageBaseAddress,
    uint ImageSize,
    uint LoadAddress,
    uint Region,
    uint AllowedMediaTypes,
    uint MediaId,
    uint TitleId,
    uint Version,
    byte Platform,
    byte DiscNumber,
    byte DiscCount,
    ushort EncryptionType,
    ushort CompressionType,
    string? TitleName = null,
    string? Publisher = null,
    string? Developer = null,
    string? Genre = null,
    string? Description = null,
    bool? HasThumbnail = null);

/// <summary><c>xbe-info</c> command payload.</summary>
internal sealed record JsonXbeInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string Path,
    uint BaseAddress,
    uint EntryPoint,
    uint SectionCount,
    uint InitFlags,
    uint CertSize,
    uint CertTimeDate,
    uint TitleId,
    string TitleName,
    uint[] AlternateTitleIds,
    uint AllowedMedia,
    uint GameRegion,
    uint GameRatings,
    uint DiskNumber,
    uint Version,
    bool HasLogoBitmap = false,
    bool HasTitleImage = false,
    bool HasSaveImage = false);

/// <summary>One side of a conversion validation report.</summary>
internal sealed record JsonValidationSide(
    string Path,
    int FileCount,
    int DirCount,
    long TotalBytes);

/// <summary>One validation issue.</summary>
internal sealed record JsonValidationIssue(
    string Type,
    string Path,
    long SourceSize,
    long OutputSize,
    string? SourceHash,
    string? OutputHash);

/// <summary><c>validate</c> command payload.</summary>
internal sealed record JsonValidation(
    int SchemaVersion,
    string Command,
    string Source,
    string Output,
    bool Checksums,
    bool Passed,
    JsonValidationSide SourceSummary,
    JsonValidationSide OutputSummary,
    List<JsonValidationIssue> Issues);

/// <summary>Full-image hashes for one input.</summary>
internal sealed record JsonImageHash(
    string Source,
    long TotalBytes,
    string Crc32,
    string Md5,
    string Sha1,
    string Sha256,
    string Sha3256);

/// <summary><c>hash</c> command payload.</summary>
internal sealed record JsonImageHashes(
    int SchemaVersion,
    string Command,
    List<JsonImageHash> Hashes);

/// <summary>One entry in a <c>verify-hashes</c> report.</summary>
internal sealed record JsonHashEntry(
    string Path,
    string Status,
    string? ExpectedSha1,
    string? ActualSha1);

/// <summary><c>verify-hashes</c> command payload.</summary>
internal sealed record JsonHashVerification(
    int SchemaVersion,
    string Command,
    string Source,
    string HashFile,
    bool Passed,
    int Checked,
    int Matched,
    int Mismatched,
    int Missing,
    int Extra,
    List<JsonHashEntry> Entries);

/// <summary><c>title-info</c> command payload.</summary>
internal sealed record JsonTitleInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string XbePath,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    uint GameRegion,
    string GameRegionName,
    uint DiskNumber,
    uint Version,
    string? Kind = null,
    string? XexPath = null,
    string? Publisher = null,
    string? Developer = null,
    string? Genre = null,
    string? Description = null,
    bool? HasThumbnail = null,
    byte? DiscCount = null);

/// <summary><c>title-info</c> command payload for a GOD/SVOD container.</summary>
internal sealed record JsonGodTitleInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string Kind,
    string HeaderPath,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    byte DiscNumber,
    byte DiscCount);

/// <summary><c>title-info</c> command payload for a single-file XBLA/XBLIG STFS package.</summary>
internal sealed record JsonStfsTitleInfo(
    int SchemaVersion,
    string Command,
    string Source,
    string Kind,
    string PackagePath,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    byte DiscNumber,
    byte DiscCount,
    uint ContentType);

/// <summary>
/// Trim/AOT-safe source-generation context for every <c>--json</c> payload.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonVersion))]
[JsonSerializable(typeof(JsonInfo))]
[JsonSerializable(typeof(JsonListing))]
[JsonSerializable(typeof(JsonChecksums))]
[JsonSerializable(typeof(JsonSectorLayout))]
[JsonSerializable(typeof(JsonRanges))]
[JsonSerializable(typeof(JsonFileTime))]
[JsonSerializable(typeof(JsonXexInfo))]
[JsonSerializable(typeof(JsonXbeInfo))]
[JsonSerializable(typeof(JsonValidation))]
[JsonSerializable(typeof(JsonImageHashes))]
[JsonSerializable(typeof(JsonHashVerification))]
[JsonSerializable(typeof(JsonTitleInfo))]
[JsonSerializable(typeof(JsonGodTitleInfo))]
[JsonSerializable(typeof(JsonStfsTitleInfo))]
internal sealed partial class JsonOutputContext : JsonSerializerContext;
