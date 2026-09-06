namespace XISOSharp.Models;

/// <summary>
/// Metadata parsed from the header and certificate of an original-Xbox
/// executable (XBEH format). All multi-byte fields are read little-endian
/// per the XBE specification (see <c>xbe.h</c> in Cxbx-Reloaded).
/// </summary>
/// <param name="BaseAddress">Image base address from the header (retail: <c>0x00010000</c>).</param>
/// <param name="EntryPoint">Entry point address from the header.</param>
/// <param name="SectionCount">Number of section headers.</param>
/// <param name="InitFlags">Init flags (e.g. <c>0x01</c> mount utility drive).</param>
/// <param name="CertSize">Certificate size in bytes (retail: 464, <c>0x1D0</c>).</param>
/// <param name="CertTimeDate">Certificate timestamp (raw DWORD).</param>
/// <param name="TitleId">Title ID from the certificate.</param>
/// <param name="TitleName">Title name from the certificate (UTF-16, up to 40 chars).</param>
/// <param name="AlternateTitleIds">Alternate title IDs (16 entries, usually zero).</param>
/// <param name="AllowedMedia">
/// Allowed-media bitmask: <c>0x01</c> hard disk, <c>0x02</c> DVD/CD,
/// <c>0x04</c> DVD-5 RO, <c>0x08</c> DVD-9 RO, <c>0x10</c> DVD-5 RW,
/// <c>0x20</c> DVD-9 RW, <c>0x40</c> dongle, <c>0x80</c> media board.
/// </param>
/// <param name="GameRegion">
/// Game-region bitmask: <c>0x01</c> North America, <c>0x02</c> Japan,
/// <c>0x04</c> rest of world (<c>0x07</c> = worldwide).
/// </param>
/// <param name="GameRatings">Game-ratings bitmask (ESRB etc., raw DWORD).</param>
/// <param name="DiskNumber">Disc number of a multi-disc title.</param>
/// <param name="Version">Game version from the certificate (raw DWORD).</param>
public sealed record XbeInfo(
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
    uint Version);
