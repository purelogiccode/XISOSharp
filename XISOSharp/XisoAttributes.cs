namespace XISOSharp;

/// <summary>
/// Maps raw XDVDFS directory-entry attribute bytes to
/// <see cref="System.IO.FileAttributes"/> for Windows VFS consumers.
/// Pure bit math — no Win32 calls, so it works on every target framework and OS.
/// </summary>
public static class XisoAttributes
{
    /// <summary>
    /// Maps the raw XDVDFS attribute byte to <see cref="System.IO.FileAttributes"/>:
    /// <see cref="System.IO.FileAttributes.ReadOnly"/> is always set (the image is
    /// read-only), <see cref="System.IO.FileAttributes.Directory"/>/
    /// <see cref="System.IO.FileAttributes.Hidden"/>/
    /// <see cref="System.IO.FileAttributes.System"/>/
    /// <see cref="System.IO.FileAttributes.Archive"/> are OR'd in from flags
    /// <c>0x10</c>/<c>0x02</c>/<c>0x04</c>/<c>0x20</c>, and
    /// <see cref="System.IO.FileAttributes.Normal"/> is added when no other
    /// standard flag is present. Reserved bits are masked first via
    /// <see cref="Constants.MaskAttributes(byte)"/>, so raw on-disk bytes map
    /// identically to <see cref="ExplorerNode.Attributes"/> and
    /// <see cref="Models.EntryInfo.Attributes"/> (already masked).
    /// </summary>
    /// <param name="attributes">Raw XDVDFS attribute byte (see <see cref="Constants"/> for flags).</param>
    /// <returns>The Windows file attributes for the entry.</returns>
    public static FileAttributes ToWindowsFileAttributes(byte attributes)
    {
        byte raw = Constants.MaskAttributes(attributes);
        FileAttributes result = FileAttributes.ReadOnly;

        if ((raw & Constants.AttributeDir) != 0)
            result |= FileAttributes.Directory;
        if ((raw & Constants.AttributeHid) != 0)
            result |= FileAttributes.Hidden;
        if ((raw & Constants.AttributeSys) != 0)
            result |= FileAttributes.System;
        if ((raw & Constants.AttributeArc) != 0)
            result |= FileAttributes.Archive;

        const FileAttributes standard = FileAttributes.Directory | FileAttributes.Hidden |
                                        FileAttributes.System | FileAttributes.Archive;
        if ((result & standard) == FileAttributes.None)
            result |= FileAttributes.Normal;

        return result;
    }
}
