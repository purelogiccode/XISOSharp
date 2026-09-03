using System.Buffers.Binary;
using System.Text;

namespace XISOSharp;

/// <summary>
/// File-extent discovery ported from <c>References/XboxKit-0.7/LibXGD/XDVDFS.cs</c>.
/// Provides <see cref="GetXisoRanges(string,long,bool)"/> / <see cref="MergeRanges"/> and file-entry enumeration
/// needed by filler/seed/wiped/trim/skeleton/ZAR and Redump rebuild.
/// </summary>
public static class XisoRanges
{
    private const long SectorSize = Constants.SectorSize;
    private const long HeaderOffset = Constants.HeaderOffset;

    // Magic for detection of the second volume descriptor sector (optional, not required for ranges
    // but kept for fidelity with XDVDFS header handling). Original: "XBOX_DVD_LAYOUT_TOOL_SIG"
    private static readonly byte[] Magic2 = "XBOX_DVD_LAYOUT_TOOL_SIG"u8.ToArray();

    // -----------------------------------------------------------------------
    // Low-level readers
    // -----------------------------------------------------------------------

    private static ushort ReadUShort(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[2];
        ReadExact(fs, buf);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static uint ReadUInt(FileStream fs)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExact(fs, buf);
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static void ReadExact(FileStream fs, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = fs.Read(buffer[offset..]);
            if (n == 0) throw new EndOfStreamException("Failed to read XDVDFS header");
            offset += n;
        }
    }

    // -----------------------------------------------------------------------
    // Recursive traversal — mirrors XDVDFS.GetValidSectors exactly
    // -----------------------------------------------------------------------

    /// <summary>
    /// Recursively collects valid filesystem and file sectors from an XISO directory tree.
    /// </summary>
    /// <param name="isoFs">Open ISO stream.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <param name="sysSectors">Collection to populate with filesystem (bone) sectors.</param>
    /// <param name="fileSectors">Collection to populate with file data sectors.</param>
    /// <param name="rootOffset">Byte offset of the current directory table.</param>
    /// <param name="rootSize">Byte size of the current directory table.</param>
    /// <param name="childOffset">Child offset within the directory table.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    public static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> sysSectors, List<uint> fileSectors,
        long rootOffset, uint rootSize, long childOffset, bool quiet)
    {
        _ = quiet;
        GetValidSectors(isoFs, isoOffset, sysSectors, fileSectors, rootOffset, rootSize, childOffset);
    }

    private static void GetValidSectors(FileStream isoFs, long isoOffset, List<uint> sysSectors, List<uint> fileSectors,
        long rootOffset, uint rootSize, long childOffset)
    {
        while (true)
        {
            if (childOffset >= rootSize) return;

            var cur = isoOffset + rootOffset + childOffset;
            var curOffset = cur / SectorSize;
            var curSize = (rootSize - childOffset + SectorSize - 1) / SectorSize;
            for (var i = curOffset; i < curOffset + curSize; i++) sysSectors.Add((uint)i);

            isoFs.Seek(cur, SeekOrigin.Begin);

            var leftChildOffset = ReadUShort(isoFs);
            if (leftChildOffset == 0xFFFF) return;
            var rightChildOffset = ReadUShort(isoFs);
            var entryOffset = ReadUInt(isoFs) * SectorSize;
            var entrySize = ReadUInt(isoFs);
            var isDirectory = ((byte)isoFs.ReadByte() & 0x10) != 0;

            if (leftChildOffset != 0)
            {
                GetValidSectors(isoFs, isoOffset, sysSectors, fileSectors, rootOffset, rootSize,
                    (long)leftChildOffset * 4);
            }

            if (isDirectory)
            {
                GetValidSectors(isoFs, isoOffset, sysSectors, fileSectors, entryOffset, entrySize, 0);
            }
            else
            {
                var fileOffset = (isoOffset + entryOffset) / SectorSize;
                var fileSize = (entrySize + SectorSize - 1) / SectorSize;
                for (var i = fileOffset; i < fileOffset + fileSize; i++) fileSectors.Add((uint)i);
            }

            if (rightChildOffset != 0)
            {
                childOffset = (long)rightChildOffset * 4;
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// Merges two sorted range lists into a single sorted, coalesced list.
    /// Ported verbatim from <c>XDVDFS.MergeRanges</c>.
    /// </summary>
    public static List<(uint Start, uint End)> MergeRanges(List<(uint Start, uint End)> a,
        List<(uint Start, uint End)> b)
    {
        var merged = new List<(uint, uint)>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
            merged.Add(a[i].Start <= b[j].Start ? a[i++] : b[j++]);
        while (i < a.Count) merged.Add(a[i++]);
        while (j < b.Count) merged.Add(b[j++]);

        if (merged.Count == 0) return merged;

        var result = new List<(uint, uint)> { merged[0] };
        for (var k = 1; k < merged.Count; k++)
        {
            var last = result[^1];
            if (merged[k].Item1 <= last.Item2 + 1)
                result[^1] = (last.Item1, Math.Max(last.Item2, merged[k].Item2));
            else
                result.Add(merged[k]);
        }

        return result;
    }

    /// <summary>
    /// Returns the set of filesystem sectors (bones) and file sectors for an XISO at
    /// <paramref name="offset"/> (byte offset of the XISO partition within the file).
    /// </summary>
    public static (List<(uint Start, uint End)> Sys, List<(uint Start, uint End)> Files) GetXisoRanges(
        FileStream isoFs, long offset, bool quiet)
    {
        _ = quiet;
        List<uint> sysSectors = [];
        List<uint> fileSectors = [];
        var headerOffset = offset + HeaderOffset;
        var headerOffsetSector = headerOffset / SectorSize;
        sysSectors.Add((uint)headerOffsetSector);

        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = ReadUInt(isoFs);
        var rootSize = ReadUInt(isoFs);

        isoFs.Seek(headerOffset + SectorSize, SeekOrigin.Begin);
        Span<byte> magic = stackalloc byte[24];
        ReadExact(isoFs, magic);
        // XBOX_DVD_LAYOUT_TOOL_SIG occupies the first bytes of the second sector when present.
        var hasMagic2 = true;
        for (var m = 0; m < Magic2.Length; m++)
        {
            if (magic[m] != Magic2[m])
            {
                hasMagic2 = false;
                break;
            }
        }

        if (hasMagic2)
            sysSectors.Add((uint)headerOffsetSector + 1);

        GetValidSectors(isoFs, offset, sysSectors, fileSectors, rootOffset * SectorSize, rootSize, 0);

        var sysRanges = BuildRanges(sysSectors);
        var fileRanges = BuildRanges(fileSectors);
        return (sysRanges, fileRanges);
    }

    private static List<(uint, uint)> BuildRanges(List<uint> sectors)
    {
        if (sectors.Count == 0) return [];
        var sorted = sectors.Distinct().Order().ToList();
        var ranges = new List<(uint, uint)>();
        var start = sorted[0];
        var prev = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            if (cur == prev + 1)
            {
                prev = cur;
            }
            else
            {
                ranges.Add((start, prev));
                start = cur;
                prev = cur;
            }
        }

        ranges.Add((start, prev));
        return ranges;
    }

    /// <summary>
    /// Returns filesystem and file ranges for an XISO at the given path and offset.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="offset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses logging.</param>
    /// <returns>Tuple of filesystem (bone) and file sector ranges.</returns>
    public static (List<(uint Start, uint End)> Sys, List<(uint Start, uint End)> Files) GetXisoRanges(
        string isoPath, long offset = 0, bool quiet = false)
    {
        _ = quiet;
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return GetXisoRanges(fs, offset, quiet);
    }

    // -----------------------------------------------------------------------
    // File-entry enumeration — mirrors XDVDFS.GetFileEntries / CollectFileEntries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns all regular file entries (not directories) with their path, byte offset
    /// and size, sorted by offset ascending (XboxKit <c>CollectFileEntries</c> order).
    /// </summary>
    public static List<(string Path, long Offset, uint Size)> GetFileEntries(FileStream isoFs, long isoOffset)
    {
        var headerOffset = isoOffset + HeaderOffset;
        isoFs.Seek(headerOffset + 20, SeekOrigin.Begin);
        var rootOffset = ReadUInt(isoFs);
        var rootSize = ReadUInt(isoFs);

        var results = new List<(string Path, long Offset, uint Size)>();
        CollectFileEntries(isoFs, isoOffset, rootOffset * SectorSize, rootSize, 0, "", results);
        results.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return results;
    }

    /// <summary>
    /// Returns all regular file entries for an XISO at the given path and offset.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="isoOffset">Byte offset of the XISO partition.</param>
    /// <returns>List of file entries with path, offset, and size.</returns>
    public static List<(string Path, long Offset, uint Size)> GetFileEntries(string isoPath, long isoOffset = 0)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return GetFileEntries(fs, isoOffset);
    }

    private static void CollectFileEntries(FileStream isoFs, long isoOffset, long dirOffset, uint dirSize,
        long childOffset, string dirPath, List<(string Path, long Offset, uint Size)> results)
    {
        if (childOffset >= dirSize) return;

        var pos = isoOffset + dirOffset + childOffset;
        isoFs.Seek(pos, SeekOrigin.Begin);

        var leftChild = ReadUShort(isoFs);
        var rightChild = ReadUShort(isoFs);
        var entrySector = ReadUInt(isoFs);
        var entrySize = ReadUInt(isoFs);
        var attributes = (byte)isoFs.ReadByte();
        var nameLength = (byte)isoFs.ReadByte();
        var nameBuf = new byte[nameLength];
        if (nameLength > 0)
        {
            var read = 0;
            while (read < nameLength)
            {
                var n = isoFs.Read(nameBuf, read, nameLength - read);
                if (n == 0) return;
                read += n;
            }
        }

        var name = Encoding.ASCII.GetString(nameBuf);
        var isDirectory = (attributes & 0x10) != 0;
        var entryOffset = entrySector * SectorSize;
        var entryPath = dirPath.Length > 0 ? dirPath + "/" + name : name;

        if (leftChild != 0 && leftChild != 0xFFFF)
            CollectFileEntries(isoFs, isoOffset, dirOffset, dirSize, (long)leftChild * 4, dirPath, results);

        if (isDirectory)
            CollectFileEntries(isoFs, isoOffset, entryOffset, entrySize, 0, entryPath, results);
        else
            results.Add((Path: entryPath, Offset: isoOffset + entryOffset, Size: entrySize));

        if (rightChild != 0 && rightChild != 0xFFFF)
            CollectFileEntries(isoFs, isoOffset, dirOffset, dirSize, (long)rightChild * 4, dirPath, results);
    }
}