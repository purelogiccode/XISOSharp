namespace XISOSharp;

/// <summary>
/// Metadata parsed from the header of an Xbox 360 executable (XEX2 format).
/// All multi-byte fields are read big-endian per the XEX2 specification
/// (see <c>XEX2</c> references in xenia/xextool).
/// </summary>
/// <param name="ModuleFlags">
/// XEX module flags: <c>0x01</c> Title, <c>0x02</c> ExportsToTitle, <c>0x04</c> SystemDebugger,
/// <c>0x08</c> DllModule, <c>0x10</c> ModulePatch, <c>0x20</c> PatchFull, <c>0x40</c> PatchDelta,
/// <c>0x80</c> UserMode.
/// </param>
/// <param name="HeaderSize">Size of the XEX header region in bytes (typically <c>0x4000</c>).</param>
/// <param name="EntryPoint">Entry point RVA from the optional header.</param>
/// <param name="ImageBaseAddress">Image base address from the optional header (e.g. <c>0x82000000</c>).</param>
/// <param name="ImageSize">Size of the loaded image in bytes (security info).</param>
/// <param name="LoadAddress">Image load address (security info).</param>
/// <param name="Region">
/// Region flags: <c>0x000000FF</c> NTSC-U, <c>0x0000FF00</c> NTSC-J, <c>0x00FF0000</c> PAL,
/// <c>0xFF000000</c> other.
/// </param>
/// <param name="AllowedMediaTypes">Bitmask of allowed media types (security info).</param>
/// <param name="MediaId">Media ID from the execution info (0 when absent).</param>
/// <param name="TitleId">Title ID from the execution info (0 when absent).</param>
/// <param name="Version">Game version from the execution info (0 when absent).</param>
/// <param name="Platform">Platform byte from the execution info (0 when absent).</param>
/// <param name="DiscNumber">Disc number of a multi-disc title (execution info).</param>
/// <param name="DiscCount">Total disc count of a multi-disc title (execution info).</param>
/// <param name="EncryptionType">File encryption type: 0 = none, 1 = normal (file format info).</param>
/// <param name="CompressionType">File compression type: 0 = none, 1 = basic, 2 = normal, 3 = delta (file format info).</param>
public sealed record XexInfo(
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
    ushort CompressionType);