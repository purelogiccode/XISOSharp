namespace XISOSharp;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// Metadata about an XISO volume descriptor (sector 32 on the disc).
/// </summary>
/// <param name="IsValid">Whether the volume magic is valid.</param>
/// <param name="RootDirSector">Sector index of the root directory table.</param>
/// <param name="RootDirSize">Size of the root directory table in bytes.</param>
/// <param name="DiscLseek">Disc lseek offset detected during probing.</param>
/// <param name="FileLength">Total size of the ISO file in bytes.</param>
/// <param name="TotalSectors">Total number of sectors in the ISO.</param>
public record VolumeInfo(
    bool IsValid,
    uint RootDirSector,
    uint RootDirSize,
    long DiscLseek,
    long FileLength,
    uint TotalSectors);

/// <summary>
/// Metadata about a single directory entry within an XISO image.
/// </summary>
/// <param name="Name">Filename of the entry.</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
/// <param name="StartSector">Sector index where the entry's data begins.</param>
/// <param name="FileSize">Size of the file data in bytes (0 for directories).</param>
/// <param name="Attributes">Raw attribute byte (see <see cref="Constants"/> for flag definitions).</param>
/// <param name="LeftChildOffset">Left child offset in the directory tree (0 if none).</param>
/// <param name="RightChildOffset">Right child offset in the directory tree (0 if none).</param>
public record EntryInfo(
    string Name,
    bool IsDirectory,
    uint StartSector,
    uint FileSize,
    byte Attributes,
    ushort LeftChildOffset,
    ushort RightChildOffset);
