namespace XISOSharp.Models;

/// <summary>
/// Title metadata read from the <c>XContent</c> header of a Games-on-Demand
/// (GOD) container. A GOD container is a header file next to a matching
/// <c>&lt;header&gt;.data</c> directory; the header alone carries the title
/// name, title ID and disc number/count, so no sector decoding or mounting is
/// needed to report them.
/// </summary>
/// <param name="HeaderPath">Absolute path of the GOD header file.</param>
/// <param name="TitleName">
/// FATX-safe title name decoded from the header (non-ASCII and control
/// characters removed, path-unsafe characters replaced with <c>_</c>). Falls
/// back to the hex title ID when the stored name is empty.
/// </param>
/// <param name="TitleId">Raw title ID (big-endian in the header).</param>
/// <param name="TitleIdHex">Title ID formatted as <c>0xXXXXXXXX</c>.</param>
/// <param name="DiscNumber">Disc number of a multi-disc title (1-based).</param>
/// <param name="DiscCount">Number of discs in the set (1 for a single-disc title).</param>
public sealed record GodTitleInfo(
    string HeaderPath,
    string TitleName,
    uint TitleId,
    string TitleIdHex,
    byte DiscNumber,
    byte DiscCount);
