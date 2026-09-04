using System.Buffers.Binary;

namespace ZARSharp;

/// <summary>
/// Shared ZArchive format constants, on-disk structures, Windows-1252 codec and
/// path helpers. Faithful pure-C# port of <c>include/zarchive/zarchivecommon.h</c>
/// (ZArchive 0.1.2, Exzap). All integers are big-endian on disk.
/// </summary>
public static class ZArchiveCommon
{
    /// <summary>Uncompressed block size: 64 KiB.</summary>
    public const int CompressedBlockSize = 64 * 1024;

    /// <summary>Entries per offset record. Must stay even.</summary>
    public const int EntriesPerOffsetRecord = 16;

    /// <summary>Node handle returned when a path is not found.</summary>
    public const uint InvalidNode = 0xFFFFFFFF;

    /// <summary>Name offset used by the root directory entry (no name).</summary>
    public const uint RootNameOffset = 0x7FFFFFFF;

    /// <summary>Maximum per-file size: 2^48 - 1.</summary>
    public const ulong MaxFileSize = 0xFFFFFFFFFFFFUL;

    /// <summary>Maximum node-name length stored (15-bit header).</summary>
    public const int MaxNameLength = 0x7FFF;

    // ------------------------------------------------------------------
    // Path helpers (port of GetNextPathNode / SplitFilenameFromPath)
    // ------------------------------------------------------------------

    /// <summary>
    /// Splits the next path node off <paramref name="path"/>. Skips leading
    /// <c>/</c> and <c>\</c> separators. Returns false when no node remains.
    /// </summary>
    public static bool GetNextPathNode(ref ReadOnlySpan<char> path, out ReadOnlySpan<char> node)
    {
        var i = 0;
        while (i < path.Length && (path[i] == '/' || path[i] == '\\'))
        {
            i++;
        }

        path = path.Slice(i);
        if (path.IsEmpty)
        {
            node = default;
            return false;
        }

        var end = 0;
        while (end < path.Length && path[end] != '/' && path[end] != '\\')
        {
            end++;
        }

        node = path.Slice(0, end);
        path = path.Slice(end);
        return true;
    }

    /// <summary>String-based overload used by the writer/tool layers.</summary>
    public static bool GetNextPathNode(ref string path, out string node)
    {
        var span = path.AsSpan();
        var ok = GetNextPathNode(ref span, out var n);
        path = span.ToString();
        node = n.ToString();
        return ok;
    }

    /// <summary>
    /// Splits <paramref name="path"/> into directory part (kept in
    /// <paramref name="path"/>) and trailing filename. Mirrors the C++
    /// backwards scan (slash itself is excluded from the filename, kept in dir).
    /// </summary>
    public static void SplitFilenameFromPath(ref ReadOnlySpan<char> path, out ReadOnlySpan<char> filename)
    {
        if (path.IsEmpty)
        {
            filename = path;
            return;
        }

        var index = path.Length - 1;
        while (true)
        {
            if (path[index] == '/' || path[index] == '\\')
            {
                index++; // slash isn't part of the filename
                break;
            }

            if (index == 0)
            {
                break;
            }

            index--;
        }

        filename = path.Slice(index);
        path = path.Slice(0, index);
    }

    /// <summary>String-based overload.</summary>
    public static void SplitFilenameFromPath(ref string path, out string filename)
    {
        var span = path.AsSpan();
        SplitFilenameFromPath(ref span, out var f);
        path = span.ToString();
        filename = f.ToString();
    }

    // ------------------------------------------------------------------
    // Name comparison (case-insensitive, A-Z folding only)
    // ------------------------------------------------------------------

    private static char FoldAscii(char c)
    {
        return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
    }

    /// <summary>Case-insensitive equality (folds A-Z only).</summary>
    public static bool CompareNodeNameBool(ReadOnlySpan<char> n1, ReadOnlySpan<char> n2)
    {
        if (n1.Length != n2.Length)
        {
            return false;
        }

        for (var i = 0; i < n1.Length; i++)
        {
            if (FoldAscii(n1[i]) != FoldAscii(n2[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Ordering comparator. Mirrors the C++ quirk exactly: on a character
    /// mismatch it returns <c>(int)(byte)c2 - (int)(byte)c1</c> (note the
    /// reversed order), and a shorter string sorts AFTER its prefix (+1).
    /// The writer's sort predicate uses <c>CompareNodeName(...) &gt; 0</c>,
    /// which yields ascending order.
    /// </summary>
    public static int CompareNodeName(ReadOnlySpan<char> n1, ReadOnlySpan<char> n2)
    {
        var min = Math.Min(n1.Length, n2.Length);
        for (var i = 0; i < min; i++)
        {
            var c1 = FoldAscii(n1[i]);
            var c2 = FoldAscii(n2[i]);
            if (c1 != c2)
            {
                return (byte)c2 - (byte)c1;
            }
        }

        if (n1.Length < n2.Length)
        {
            return 1;
        }

        if (n1.Length > n2.Length)
        {
            return -1;
        }

        return 0;
    }

    /// <summary>
    /// Byte-wise comparator over raw Windows-1252 bytes. Used by the reader so
    /// non-ASCII names compare exactly like the C++ <c>string_view</c> version.
    /// </summary>
    public static int CompareNodeName(ReadOnlySpan<byte> n1, ReadOnlySpan<byte> n2)
    {
        var min = Math.Min(n1.Length, n2.Length);
        for (var i = 0; i < min; i++)
        {
            var c1 = n1[i];
            var c2 = n2[i];
            if (c1 >= (byte)'A' && c1 <= (byte)'Z')
            {
                c1 += (byte)('a' - 'A');
            }

            if (c2 >= (byte)'A' && c2 <= (byte)'Z')
            {
                c2 += (byte)('a' - 'A');
            }

            if (c1 != c2)
            {
                return (int)c2 - (int)c1;
            }
        }

        if (n1.Length < n2.Length)
        {
            return 1;
        }

        if (n1.Length > n2.Length)
        {
            return -1;
        }

        return 0;
    }

    /// <summary>Byte-wise case-insensitive equality.</summary>
    public static bool CompareNodeNameBool(ReadOnlySpan<byte> n1, ReadOnlySpan<byte> n2)
    {
        if (n1.Length != n2.Length)
        {
            return false;
        }

        for (var i = 0; i < n1.Length; i++)
        {
            var c1 = n1[i];
            var c2 = n2[i];
            if (c1 >= (byte)'A' && c1 <= (byte)'Z')
            {
                c1 += (byte)('a' - 'A');
            }

            if (c2 >= (byte)'A' && c2 <= (byte)'Z')
            {
                c2 += (byte)('a' - 'A');
            }

            if (c1 != c2)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Pure-C# Windows-1252 codec (~40-line table, no CodePages package)
    // ------------------------------------------------------------------

    // Decode table for 0x80-0x9F. Undefined slots map to the C1 control range.
    private static readonly char[] Cp1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    /// <summary>Decodes Windows-1252 bytes to a .NET string.</summary>
    public static string Decode1252(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b switch
            {
                < 0x80 => (char)b,
                < 0xA0 => Cp1252High[b - 0x80],
                _ => (char)b,
            };
        }

        return new string(chars);
    }

    /// <summary>
    /// Encodes a .NET string to Windows-1252 bytes. Characters with no
    /// representation fall back to <c>?</c> (0x3F).
    /// </summary>
    public static byte[] Encode1252(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return [];
        }

        var outBytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            outBytes[i] = EncodeChar1252(text[i]);
        }

        return outBytes;
    }

    private static byte EncodeChar1252(char c)
    {
        if (c < 0x80)
        {
            return (byte)c;
        }

        if (c >= 0xA0 && c <= 0xFF)
        {
            return (byte)c;
        }

        for (var i = 0; i < Cp1252High.Length; i++)
        {
            if (Cp1252High[i] == c)
            {
                return (byte)(0x80 + i);
            }
        }

        return (byte)'?';
    }

    // ------------------------------------------------------------------
    // Big-endian primitives
    // ------------------------------------------------------------------

    /// <summary>Reads a big-endian UInt16.</summary>
    public static ushort ReadU16Be(ReadOnlySpan<byte> src)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(src);
    }

    /// <summary>Reads a big-endian UInt32.</summary>
    public static uint ReadU32Be(ReadOnlySpan<byte> src)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(src);
    }

    /// <summary>Reads a big-endian UInt64.</summary>
    public static ulong ReadU64Be(ReadOnlySpan<byte> src)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(src);
    }

    /// <summary>Writes a big-endian UInt16.</summary>
    public static void WriteU16Be(Span<byte> dst, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst, value);
    }

    /// <summary>Writes a big-endian UInt32.</summary>
    public static void WriteU32Be(Span<byte> dst, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst, value);
    }

    /// <summary>Writes a big-endian UInt64.</summary>
    public static void WriteU64Be(Span<byte> dst, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(dst, value);
    }
}