using System.Diagnostics.CodeAnalysis;
using XISOSharp.Models;
using SharpGodHeader = StfsSharp.GodHeader;
using SharpGodTitleInfo = StfsSharp.Models.GodTitleInfo;
using SharpGodVolumeInfo = StfsSharp.Models.GodVolumeInfo;

namespace XISOSharp;

/// <summary>
/// Reads the <c>XContent</c> header of a Games-on-Demand (GOD/SVOD) container.
/// A GOD container is a header file named by content hash next to a matching
/// <c>&lt;header&gt;.data</c> directory. The header is plaintext and carries the
/// title name, title ID and disc number/count, so this parser never decodes a
/// data sector and never needs the STFS/SVOD block map.
/// </summary>
/// <remarks>
/// This is a compatibility facade over the standalone
/// <see href="https://www.nuget.org/packages/StfsSharp">StfsSharp</see>
/// package. The public XISOSharp model types are preserved and mapped from the
/// StfsSharp models; they will be removed in XISOSharp 2.0.0.
/// </remarks>
public static class GodHeader
{
    /// <summary>Offset of the big-endian title ID in the header.</summary>
    public const int TitleIdOffset = SharpGodHeader.TitleIdOffset;

    /// <summary>Offset of the disc-number byte in the header.</summary>
    public const int DiscNumberOffset = SharpGodHeader.DiscNumberOffset;

    /// <summary>Offset of the disc-count byte in the header.</summary>
    public const int DiscCountOffset = SharpGodHeader.DiscCountOffset;

    /// <summary>Offset of the UTF-16BE title-name field in the header.</summary>
    public const int TitleNameOffset = SharpGodHeader.TitleNameOffset;

    /// <summary>Length in bytes of the UTF-16BE title-name field.</summary>
    public const int TitleNameLength = SharpGodHeader.TitleNameLength;

    /// <summary>
    /// Minimum header length needed to read the title-name field
    /// (<see cref="TitleNameOffset"/> + <see cref="TitleNameLength"/>).
    /// </summary>
    public const int MinimumHeaderLength = SharpGodHeader.MinimumHeaderLength;

    /// <summary>
    /// Returns true when <paramref name="path"/> is a GOD header file or its
    /// <c>.data</c> directory: the header exists, the sibling
    /// <c>&lt;header&gt;.data</c> directory exists, and the header carries a
    /// recognized XContent signature/content type.
    /// </summary>
    public static bool IsGodContainer(string path) => SharpGodHeader.IsGodContainer(path);

    /// <summary>
    /// Reads the SVOD sector mapping from a GOD header file or its
    /// <c>.data</c> directory. Returns <c>false</c> for a single-file STFS
    /// package, a non-SVOD volume, a missing/truncated fragment file, or an
    /// unreadable header.
    /// </summary>
    /// <param name="path">GOD header file or the matching <c>.data</c> directory.</param>
    /// <param name="info">Parsed volume mapping when the method returns <c>true</c>.</param>
    public static bool TryReadVolume(string path, [NotNullWhen(true)] out GodVolumeInfo? info) =>
        TryReadVolume(path, FileShare.Read, out info);

    /// <summary>
    /// Reads the SVOD sector mapping with a caller-chosen share mode for the
    /// header probe.
    /// </summary>
    /// <param name="path">GOD header file or the matching <c>.data</c> directory.</param>
    /// <param name="share">File sharing mode used while reading the header.</param>
    /// <param name="info">Parsed volume mapping when the method returns <c>true</c>.</param>
    public static bool TryReadVolume(string path, FileShare share,
        [NotNullWhen(true)] out GodVolumeInfo? info)
    {
        if (!SharpGodHeader.TryReadVolume(path, share, out SharpGodVolumeInfo? volume))
        {
            info = null;
            return false;
        }

        info = Map(volume);
        return true;
    }

    /// <summary>
    /// Reads title metadata from a GOD header path or its <c>.data</c>
    /// directory. Returns <c>false</c> for a path that is not a GOD container,
    /// a truncated/invalid header, or an unreadable file.
    /// </summary>
    /// <param name="path">GOD header file or the matching <c>.data</c> directory.</param>
    /// <param name="info">Parsed metadata when the method returns <c>true</c>.</param>
    public static bool TryReadTitle(string path, [NotNullWhen(true)] out GodTitleInfo? info)
    {
        if (!SharpGodHeader.TryReadTitle(path, out SharpGodTitleInfo? title))
        {
            info = null;
            return false;
        }

        info = Map(title);
        return true;
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryReadTitle"/> that returns
    /// <c>null</c> when the path is not a readable GOD container.
    /// </summary>
    public static GodTitleInfo? ReadTitle(string path) =>
        TryReadTitle(path, out GodTitleInfo? info) ? info : null;

    internal static GodTitleInfo Map(SharpGodTitleInfo title) => new(
        title.HeaderPath,
        title.TitleName,
        title.TitleId,
        title.TitleIdHex,
        title.DiscNumber,
        title.DiscCount);

    internal static GodVolumeInfo Map(SharpGodVolumeInfo volume) => new(
        volume.HeaderPath,
        volume.DataDirectory,
        volume.DataFileCount,
        volume.StartingBlock,
        volume.DataBlockCount,
        volume.EnhancedGdf,
        volume.Length,
        volume.Parts);
}
