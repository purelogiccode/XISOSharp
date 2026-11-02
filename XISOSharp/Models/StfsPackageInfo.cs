namespace XISOSharp.Models;

/// <summary>
/// Summary of a single-file Xbox 360 STFS package (XBLA or XBLIG): title
/// metadata from the <c>XContent</c> header plus the STFS volume layout.
/// </summary>
/// <param name="PackagePath">Absolute path of the package file.</param>
/// <param name="Kind">Package kind (XBLA or XBLIG).</param>
/// <param name="TitleName">FATX-safe title name (falls back to the hex title ID when empty).</param>
/// <param name="TitleId">Raw title ID (big-endian in the header).</param>
/// <param name="TitleIdHex">Title ID formatted as <c>0xXXXXXXXX</c>.</param>
/// <param name="DiscNumber">Disc number of a multi-disc title.</param>
/// <param name="DiscCount">Number of discs in the set.</param>
/// <param name="ContentType">Raw XContent content type.</param>
/// <param name="PackageLength">Package file length in bytes.</param>
/// <param name="HeaderSize">Raw <c>XContent</c> header size.</param>
/// <param name="FirstBlockOffset">File offset of STFS block 0 (the first hash table).</param>
/// <param name="TotalBlockCount">Number of logical data blocks (hash tables excluded).</param>
/// <param name="FreeBlockCount">Number of free logical data blocks reported by the volume descriptor.</param>
/// <param name="FileTableBlockCount">Number of blocks occupied by the file table.</param>
/// <param name="FileTableBlockNumber">First block of the file table.</param>
/// <param name="IsReadOnlyFormat">Whether the package uses one hash table per level (read-only format).</param>
/// <param name="FileCount">Number of file entries in the file table.</param>
/// <param name="DirectoryCount">Number of directory entries in the file table.</param>
/// <param name="TotalFileSize">Sum of every file's declared size.</param>
public sealed record StfsPackageInfo(
    string PackagePath,
    StfsPackageKind Kind,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    byte DiscNumber,
    byte DiscCount,
    uint ContentType,
    long PackageLength,
    uint HeaderSize,
    long FirstBlockOffset,
    uint TotalBlockCount,
    uint FreeBlockCount,
    ushort FileTableBlockCount,
    uint FileTableBlockNumber,
    bool IsReadOnlyFormat,
    int FileCount,
    int DirectoryCount,
    long TotalFileSize);
