namespace ZARSharp;

/// <summary>
/// On-disk offset record: full 64-bit base offset plus 16 size entries
/// (each storing <c>compressedSize - 1</c>). 40 bytes, big-endian.
/// </summary>
public struct CompressionOffsetRecord
{
    /// <summary>Size on disk in bytes.</summary>
    public const int SizeOnDisk = 8 + 2 * ZArchiveCommon.EntriesPerOffsetRecord;

    /// <summary>Base output offset of the first block in this record.</summary>
    public ulong BaseOffset;

    /// <summary>Per-block <c>compressedSize - 1</c> entries (always 16 used).</summary>
    public ushort[] Sizes;

    /// <summary>Creates a record with a zeroed size table.</summary>
    /// <param name="baseOffset">Base output offset.</param>
    public CompressionOffsetRecord(ulong baseOffset)
    {
        BaseOffset = baseOffset;
        Sizes = new ushort[ZArchiveCommon.EntriesPerOffsetRecord];
    }

    /// <summary>Reads one record from <paramref name="src"/> (40 bytes).</summary>
    public static CompressionOffsetRecord ReadFrom(ReadOnlySpan<byte> src)
    {
        var rec = new CompressionOffsetRecord
        {
            BaseOffset = ZArchiveCommon.ReadU64BE(src),
            Sizes = new ushort[ZArchiveCommon.EntriesPerOffsetRecord],
        };
        for (int i = 0; i < ZArchiveCommon.EntriesPerOffsetRecord; i++)
        {
            rec.Sizes[i] = ZArchiveCommon.ReadU16BE(src.Slice(8 + i * 2));
        }

        return rec;
    }

    /// <summary>Writes this record to <paramref name="dst"/> (40 bytes).</summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        ZArchiveCommon.WriteU64BE(dst, BaseOffset);
        for (int i = 0; i < ZArchiveCommon.EntriesPerOffsetRecord; i++)
        {
            ZArchiveCommon.WriteU16BE(dst.Slice(8 + i * 2), Sizes[i]);
        }
    }
}

/// <summary>
/// File/directory tree entry. 16 bytes = 4 x u32 big-endian. The C++
/// serializer treats both variants as the same 3 x u32 layout.
/// </summary>
public struct FileDirectoryEntry
{
    /// <summary>Size on disk in bytes.</summary>
    public const int SizeOnDisk = 16;

    /// <summary>MSB 0x80000000 = file; low 31 bits = name-table offset.</summary>
    public uint NameOffsetAndTypeFlag;

    /// <summary>Meaning depends on <see cref="IsFile"/> (see spec).</summary>
    public uint Field1;

    /// <summary>Meaning depends on <see cref="IsFile"/> (see spec).</summary>
    public uint Field2;

    /// <summary>Meaning depends on <see cref="IsFile"/> (see spec).</summary>
    public uint Field3;

    /// <summary>True for file entries.</summary>
    public readonly bool IsFile => (NameOffsetAndTypeFlag & 0x80000000) != 0;

    /// <summary>Name-table offset (low 31 bits).</summary>
    public readonly uint NameOffset => NameOffsetAndTypeFlag & 0x7FFFFFFF;

    /// <summary>File offset low 32 bits (files only).</summary>
    public readonly uint FileOffsetLow => Field1;

    /// <summary>File size low 32 bits (files only).</summary>
    public readonly uint FileSizeLow => Field2;

    /// <summary>Upper 16 bits = size extension, lower 16 = offset extension.</summary>
    public readonly uint FileOffsetAndSizeHigh => Field3;

    /// <summary>First child node index (directories only).</summary>
    public readonly uint NodeStartIndex => Field1;

    /// <summary>Child count (directories only).</summary>
    public readonly uint Count => Field2;

    /// <summary>Reserved (directories only, always 0).</summary>
    public readonly uint Reserved => Field3;

    /// <summary>Sets the type flag and name offset.</summary>
    public void SetTypeAndNameOffset(bool isFile, uint nameOffset)
    {
        NameOffsetAndTypeFlag = (nameOffset & 0x7FFFFFFF) | (isFile ? 0x80000000u : 0u);
    }

    /// <summary>Uncompressed file offset (logical input offset).</summary>
    public readonly ulong GetFileOffset()
    {
        ulong off = Field1;
        off |= ((ulong)(Field3 & 0xFFFF)) << 32;
        return off;
    }

    /// <summary>Uncompressed file size.</summary>
    public readonly ulong GetFileSize()
    {
        ulong size = Field2;
        size |= ((ulong)(Field3 & 0xFFFF0000)) << 16;
        return size;
    }

    /// <summary>Sets the uncompressed file offset (preserves size bits).</summary>
    public void SetFileOffset(ulong fileOffset)
    {
        Field1 = (uint)fileOffset;
        Field3 = (Field3 & 0xFFFF0000) | ((uint)(fileOffset >> 32) & 0xFFFF);
    }

    /// <summary>Sets the uncompressed file size (preserves offset bits).</summary>
    public void SetFileSize(ulong fileSize)
    {
        Field2 = (uint)fileSize;
        Field3 = (Field3 & 0x0000FFFF) | ((uint)(fileSize >> 16) & 0xFFFF0000);
    }

    /// <summary>Reads one entry from <paramref name="src"/> (16 bytes).</summary>
    public static FileDirectoryEntry ReadFrom(ReadOnlySpan<byte> src) => new()
    {
        NameOffsetAndTypeFlag = ZArchiveCommon.ReadU32BE(src),
        Field1 = ZArchiveCommon.ReadU32BE(src.Slice(4)),
        Field2 = ZArchiveCommon.ReadU32BE(src.Slice(8)),
        Field3 = ZArchiveCommon.ReadU32BE(src.Slice(12)),
    };

    /// <summary>Writes this entry to <paramref name="dst"/> (16 bytes).</summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        ZArchiveCommon.WriteU32BE(dst, NameOffsetAndTypeFlag);
        ZArchiveCommon.WriteU32BE(dst.Slice(4), Field1);
        ZArchiveCommon.WriteU32BE(dst.Slice(8), Field2);
        ZArchiveCommon.WriteU32BE(dst.Slice(12), Field3);
    }
}

/// <summary>Section offset + size pair.</summary>
public struct OffsetInfo
{
    /// <summary>Section offset.</summary>
    public ulong Offset;

    /// <summary>Section size.</summary>
    public ulong Size;

    /// <summary>True when the section lies fully inside a file of <paramref name="fileSize"/> bytes.</summary>
    public readonly bool IsWithinValidRange(ulong fileSize)
    {
        unchecked
        {
            return (Offset + Size) <= fileSize;
        }
    }
}

/// <summary>
/// Archive footer. 144 bytes. Field order on disk: six offset infos
/// (compressedData, offsetRecords, names, fileTree, metaDirectory, metaData),
/// then 32-byte integrity hash, u64 totalSize, u32 version, u32 magic
/// (magic/version at the END).
/// </summary>
public struct Footer
{
    /// <summary>Size on disk in bytes.</summary>
    public const int SizeOnDisk = (16 * 6) + 32 + 8 + 4 + 4;

    /// <summary>Magic value (last u32).</summary>
    public const uint KMagic = 0x169f52d6;

    /// <summary>Version value (second-to-last u32).</summary>
    public const uint KVersion1 = 0x61bf3a01;

    /// <summary>Compressed data section.</summary>
    public OffsetInfo SectionCompressedData;

    /// <summary>Offset-record section.</summary>
    public OffsetInfo SectionOffsetRecords;

    /// <summary>Name-table section.</summary>
    public OffsetInfo SectionNames;

    /// <summary>File-tree section.</summary>
    public OffsetInfo SectionFileTree;

    /// <summary>Meta-directory section (always empty in 0.1.2).</summary>
    public OffsetInfo SectionMetaDirectory;

    /// <summary>Meta-data section (always empty in 0.1.2).</summary>
    public OffsetInfo SectionMetaData;

    /// <summary>SHA-256 over every preceding output byte + zeroed footer.</summary>
    public byte[] IntegrityHash;

    /// <summary>Total file size (including footer).</summary>
    public ulong TotalSize;

    /// <summary>Format version.</summary>
    public uint Version;

    /// <summary>Magic.</summary>
    public uint Magic;

    /// <summary>Reads a footer from <paramref name="src"/> (144 bytes).</summary>
    public static Footer ReadFrom(ReadOnlySpan<byte> src)
    {
        Footer f = new()
        {
            IntegrityHash = new byte[32],
        };
        int o = 0;
        f.SectionCompressedData = ReadInfo(src.Slice(o));
        o += 16;
        f.SectionOffsetRecords = ReadInfo(src.Slice(o));
        o += 16;
        f.SectionNames = ReadInfo(src.Slice(o));
        o += 16;
        f.SectionFileTree = ReadInfo(src.Slice(o));
        o += 16;
        f.SectionMetaDirectory = ReadInfo(src.Slice(o));
        o += 16;
        f.SectionMetaData = ReadInfo(src.Slice(o));
        o += 16;
        src.Slice(o, 32).CopyTo(f.IntegrityHash.AsSpan());
        o += 32;
        f.TotalSize = ZArchiveCommon.ReadU64BE(src.Slice(o));
        o += 8;
        f.Version = ZArchiveCommon.ReadU32BE(src.Slice(o));
        o += 4;
        f.Magic = ZArchiveCommon.ReadU32BE(src.Slice(o));
        return f;

        static OffsetInfo ReadInfo(ReadOnlySpan<byte> s) => new()
        {
            Offset = ZArchiveCommon.ReadU64BE(s),
            Size = ZArchiveCommon.ReadU64BE(s.Slice(8)),
        };
    }

    /// <summary>Writes this footer to <paramref name="dst"/> (144 bytes).</summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        int o = 0;
        WriteInfo(dst.Slice(o), SectionCompressedData);
        o += 16;
        WriteInfo(dst.Slice(o), SectionOffsetRecords);
        o += 16;
        WriteInfo(dst.Slice(o), SectionNames);
        o += 16;
        WriteInfo(dst.Slice(o), SectionFileTree);
        o += 16;
        WriteInfo(dst.Slice(o), SectionMetaDirectory);
        o += 16;
        WriteInfo(dst.Slice(o), SectionMetaData);
        o += 16;
        IntegrityHash.AsSpan(0, 32).CopyTo(dst.Slice(o, 32));
        o += 32;
        ZArchiveCommon.WriteU64BE(dst.Slice(o), TotalSize);
        o += 8;
        ZArchiveCommon.WriteU32BE(dst.Slice(o), Version);
        o += 4;
        ZArchiveCommon.WriteU32BE(dst.Slice(o), Magic);

        static void WriteInfo(Span<byte> d, OffsetInfo i)
        {
            ZArchiveCommon.WriteU64BE(d, i.Offset);
            ZArchiveCommon.WriteU64BE(d.Slice(8), i.Size);
        }
    }
}