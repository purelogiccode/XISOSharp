using System.Runtime.InteropServices;

namespace ZARSharp.Zstd;

/// <summary>
/// Pure-C# zstd block/frame decompressor (RFC 8878, "Zstandard Compression").
/// Supports standard frames (raw, RLE and compressed blocks; Huffman and FSE
/// entropy coding; predefined/RLE/FSE/repeat sequence tables; content
/// checksums; multi-frame concatenation) and skippable frames. Dictionaries
/// are not supported. Decoder limits default to 512 MiB windows and 512 MiB
/// frames (see <see cref="ZstdDecoderOptions"/>); ZArchive needs only 64 KiB.
/// </summary>
public static class ZstdDecompressor
{
    private const uint ZstdMagic = 0xFD2FB528;
    private const uint SkippableMask = 0xFFFFFFF0;
    private const uint SkippableBase = 0x184D2A50;
    private const int BlockHeaderSize = 3;
    private const int MaxBlockSizeLimit = 128 * 1024;

    // Default decoder limits (documented; ZArchive needs only 64 KiB).
    // The window cap is a validity bound only: history is retained in full,
    // so raising it never changes allocation behavior beyond the frame cap.
    private const ulong DefaultMaxWindowSize = 512UL * 1024 * 1024;
    private const ulong DefaultMaxFrameContentSize = 512UL * 1024 * 1024;

    // ------------------------------------------------------------------
    // Sequence code tables (verified against the reference implementation)
    // ------------------------------------------------------------------

    private const int MaxLL = 35;
    private const int MaxML = 52;
    private const int MaxOff = 31;

    private static readonly byte[] LLBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    private static readonly uint[] LLBase =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
        8192, 16384, 32768, 65536,
    ];

    private static readonly byte[] MLBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    private static readonly uint[] MLBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051,
        4099, 8195, 16387, 32771, 65539,
    ];

    private static ulong OffsetBase(int code) =>
        code == 0 ? 0UL : code == 1 ? 1UL : (1UL << code) - 3;

    private static readonly short[] LLDefaultNorm =
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    ];

    private static readonly short[] MLDefaultNorm =
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    ];

    private static readonly short[] OFDefaultNorm =
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ];

    private sealed class SeqTable
    {
        public int TableLog;
        public uint[] Bases = [];
        public byte[] ExtraBits = [];
        public byte[] NumBits = [];
        public int[] NewState = [];
    }

    private static readonly SeqTable LLDefaultTable = BuildSeqTable(LLDefaultNorm, 35, 6, LLBase, LLBits);
    private static readonly SeqTable MLDefaultTable = BuildSeqTable(MLDefaultNorm, 52, 6, MLBase, MLBits);
    private static readonly SeqTable OFDefaultTable = BuildOFTable(OFDefaultNorm, 28, 5);

    private static SeqTable BuildSeqTable(
        short[] norms, int maxSymbol, int log, uint[] bases, byte[] extras)
    {
        var generic = ZstdFse.BuildTable(norms, maxSymbol, log);
        int size = 1 << log;
        var table = new SeqTable
        {
            TableLog = log,
            Bases = new uint[size],
            ExtraBits = new byte[size],
            NumBits = new byte[size],
            NewState = new int[size],
        };
        for (int i = 0; i < size; i++)
        {
            int sym = generic.Symbols[i];
            table.Bases[i] = bases[sym];
            table.ExtraBits[i] = extras[sym];
            table.NumBits[i] = generic.NumBits[i];
            table.NewState[i] = generic.NewState[i];
        }

        return table;
    }

    private static SeqTable BuildOFTable(short[] norms, int maxSymbol, int log)
    {
        var generic = ZstdFse.BuildTable(norms, maxSymbol, log);
        int size = 1 << log;
        var table = new SeqTable
        {
            TableLog = log,
            Bases = new uint[size],
            ExtraBits = new byte[size],
            NumBits = new byte[size],
            NewState = new int[size],
        };
        for (int i = 0; i < size; i++)
        {
            int sym = generic.Symbols[i];
            table.Bases[i] = (uint)OffsetBase(sym);
            table.ExtraBits[i] = (byte)sym;
            table.NumBits[i] = generic.NumBits[i];
            table.NewState[i] = generic.NewState[i];
        }

        return table;
    }

    private static SeqTable BuildRleSeqTable(uint baseline, byte extraBits) => new()
    {
        TableLog = 0,
        Bases = [baseline],
        ExtraBits = [extraBits],
        NumBits = [0],
        NewState = [0],
    };

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Decompresses concatenated zstd frames, returning the output.
    /// </summary>
    /// <exception cref="ZstdException">On corrupt input or unsupported features.</exception>
    public static byte[] Decompress(byte[] src, int offset, int length) =>
        Decompress(src, offset, length, ZstdDecoderOptions.Default);

    /// <summary>
    /// Decompresses concatenated zstd frames, returning the output,
    /// enforcing the limits in <paramref name="options"/>.
    /// </summary>
    /// <exception cref="ZstdException">On corrupt input or unsupported features.</exception>
    public static byte[] Decompress(byte[] src, int offset, int length, ZstdDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(options);

        var output = new List<byte>();
        int pos = DecompressFrames(src, offset, length, output, null, options);
        if (pos != offset + length)
        {
            throw new ZstdException("Trailing data after zstd frame.");
        }

        return [.. output];
    }

    /// <summary>Decompresses concatenated zstd frames, returning the output.</summary>
    public static byte[] Decompress(byte[] src) =>
        Decompress(src, 0, src is null ? 0 : src.Length);

    /// <summary>
    /// Decompresses concatenated zstd frames, returning the output,
    /// enforcing the limits in <paramref name="options"/>.
    /// </summary>
    public static byte[] Decompress(byte[] src, ZstdDecoderOptions options) =>
        Decompress(src, 0, src is null ? 0 : src.Length, options);

    /// <summary>
    /// Decompresses exactly one frame region into <paramref name="dst"/>,
    /// which must fill exactly (like <c>ZSTD_decompress</c> into a
    /// content-sized buffer). The input must be exactly one frame.
    /// </summary>
    public static void DecompressExact(
        byte[] src, int srcOffset, int srcLength,
        byte[] dst, int dstOffset, int dstLength) =>
        DecompressExact(src, srcOffset, srcLength, dst, dstOffset, dstLength, ZstdDecoderOptions.Default);

    /// <summary>
    /// Decompresses exactly one frame region into <paramref name="dst"/>,
    /// which must fill exactly (like <c>ZSTD_decompress</c> into a
    /// content-sized buffer). The input must be exactly one frame.
    /// Enforces the limits in <paramref name="options"/>.
    /// </summary>
    public static void DecompressExact(
        byte[] src, int srcOffset, int srcLength,
        byte[] dst, int dstOffset, int dstLength, ZstdDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var output = new List<byte>(dstLength);
        int pos = DecompressFrame(src, srcOffset, srcLength, output, (ulong)dstLength, options);
        if (pos != srcOffset + srcLength)
        {
            throw new ZstdException("Trailing data after zstd frame.");
        }

        if (output.Count != dstLength)
        {
            throw new ZstdException(
                $"Decompressed size {output.Count} != expected {dstLength}.");
        }

        output.CopyTo(dst, dstOffset);
    }

    // ------------------------------------------------------------------
    // Frames
    // ------------------------------------------------------------------

    private sealed class FrameContext
    {
        public ulong WindowSize;
        public SeqTable? LLTable;
        public SeqTable? OFTable;
        public SeqTable? MLTable;
        public ZstdHuffman.HuffmanTable? HuffmanTable;
        public readonly ulong[] RepeatOffsets = [1, 4, 8];
    }

    private static int DecompressFrames(
        byte[] src, int offset, int length, List<byte> output, ulong? exactSize,
        ZstdDecoderOptions options)
    {
        int end = offset + length;
        int pos = offset;
        bool anyFrame = false;
        while (pos < end)
        {
            if (end - pos < 4)
            {
                throw new ZstdException("Truncated zstd frame magic.");
            }

            uint magic = ReadU32LE(src, pos);
            if ((magic & SkippableMask) == SkippableBase)
            {
                if (end - pos < 8)
                {
                    throw new ZstdException("Truncated skippable frame.");
                }

                uint skip = ReadU32LE(src, pos + 4);
                if (skip > (uint)(end - pos - 8))
                {
                    throw new ZstdException("Truncated skippable frame.");
                }

                pos += 8 + (int)skip;
                continue;
            }

            if (magic != ZstdMagic)
            {
                throw new ZstdException($"Bad zstd magic 0x{magic:X8}.");
            }

            pos = DecompressFrame(src, pos, end - pos, output, exactSize, options);
            anyFrame = true;
            if (exactSize.HasValue)
            {
                break; // exact mode: single frame
            }
        }

        if (!anyFrame)
        {
            throw new ZstdException("No zstd frame found.");
        }

        return pos;
    }

    private static int DecompressFrame(
        byte[] src, int offset, int length, List<byte> output, ulong? exactSize,
        ZstdDecoderOptions options)
    {
        int end = offset + length;
        int pos = offset + 4; // magic already validated by caller... (validated below for exact path)
        if (ReadU32LE(src, offset) != ZstdMagic)
        {
            throw new ZstdException("Bad zstd magic.");
        }

        if (pos >= end)
        {
            throw new ZstdException("Truncated zstd frame header.");
        }

        byte descriptor = src[pos++];
        if ((descriptor & 0x08) != 0)
        {
            throw new ZstdException("Reserved frame flag set.");
        }

        int fcsFlag = (descriptor >> 6) & 3;
        bool singleSegment = (descriptor & 0x20) != 0;
        bool checksumFlag = (descriptor & 0x04) != 0;
        int dictFlag = descriptor & 3;

        var ctx = new FrameContext();

        if (singleSegment)
        {
            ctx.WindowSize = 0; // filled from FCS below
        }
        else
        {
            if (pos >= end)
            {
                throw new ZstdException("Truncated window descriptor.");
            }

            byte wd = src[pos++];
            uint windowLog = 10 + (uint)(wd >> 3);
            ulong windowBase = 1UL << (int)windowLog;
            ulong windowAdd = (windowBase / 8) * (uint)(wd & 7);
            ctx.WindowSize = windowBase + windowAdd;
        }

        if (dictFlag != 0)
        {
            throw new ZstdException("zstd dictionaries are not supported.");
        }

        int fcsSize = fcsFlag switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };

        ulong fcs = 0;
        bool fcsKnown = fcsSize != 0;
        if (fcsKnown)
        {
            if (pos + fcsSize > end)
            {
                throw new ZstdException("Truncated frame content size.");
            }

            fcs = ReadUIntLE(src, pos, fcsSize);
            if (fcsSize == 2)
            {
                fcs += 256;
            }

            pos += fcsSize;
            if (singleSegment)
            {
                ctx.WindowSize = fcs;
            }
        }

        if (ctx.WindowSize == 0 && !singleSegment)
        {
            throw new ZstdException("Invalid zstd window size.");
        }

        if (ctx.WindowSize > options.MaxWindowSize)
        {
            throw new ZstdException(
                $"zstd window size {ctx.WindowSize} exceeds decoder limit {options.MaxWindowSize}.");
        }

        if (fcsKnown && fcs > options.MaxFrameContentSize)
        {
            throw new ZstdException(
                $"zstd frame content size {fcs} exceeds decoder limit {options.MaxFrameContentSize}.");
        }

        if (exactSize.HasValue && (!fcsKnown || fcs != exactSize.Value))
        {
            // The caller demands an exact output size (e.g. 64 KiB blocks);
            // a mismatch means this is not the expected single frame.
            throw new ZstdException(
                $"zstd frame content size {fcs} != expected {exactSize.Value}.");
        }

        ulong maxBlock = ctx.WindowSize;
        if (maxBlock > MaxBlockSizeLimit)
        {
            maxBlock = MaxBlockSizeLimit;
        }

        int frameStart = output.Count;
        ulong frameCap = fcsKnown ? fcs : options.MaxFrameContentSize;
        bool lastBlock = false;
        while (!lastBlock)
        {
            if (pos + BlockHeaderSize > end)
            {
                throw new ZstdException("Truncated block header.");
            }

            uint header = (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16));
            pos += BlockHeaderSize;
            lastBlock = (header & 1) != 0;
            int blockType = (int)((header >> 1) & 3);
            int blockSize = (int)(header >> 3);
            if ((ulong)blockSize > maxBlock && (blockType == 0 || blockType == 1))
            {
                throw new ZstdException("zstd block too large.");
            }

            switch (blockType)
            {
                case 0: // Raw
                    if (pos + blockSize > end)
                    {
                        throw new ZstdException("Truncated raw block.");
                    }

                    for (int i = 0; i < blockSize; i++)
                    {
                        output.Add(src[pos + i]);
                    }

                    pos += blockSize;
                    break;

                case 1: // RLE
                    if (pos + 1 > end)
                    {
                        throw new ZstdException("Truncated RLE block.");
                    }

                    byte value = src[pos++];
                    for (int i = 0; i < blockSize; i++)
                    {
                        output.Add(value);
                    }

                    break;

                case 2: // Compressed
                    if (pos + blockSize > end)
                    {
                        throw new ZstdException("Truncated compressed block.");
                    }

                    DecompressBlock(src, pos, blockSize, output, frameStart, ctx, maxBlock);
                    pos += blockSize;
                    break;

                default:
                    throw new ZstdException("Reserved zstd block type.");
            }

            if ((ulong)(output.Count - frameStart) > frameCap)
            {
                throw new ZstdException("zstd frame content size mismatch.");
            }
        }

        if (checksumFlag)
        {
            if (pos + 4 > end)
            {
                throw new ZstdException("Truncated content checksum.");
            }

            int contentStart = frameStart;
            int contentLen = output.Count - contentStart;
            byte[] flat = output.ToArray();
            uint actual = (uint)ZstdXxh64.Hash64(flat, contentStart, contentLen);
            uint expected = ReadU32LE(src, pos);
            pos += 4;
            if (actual != expected)
            {
                throw new ZstdException("zstd content checksum mismatch.");
            }
        }

        if (fcsKnown && (ulong)(output.Count - frameStart) != fcs)
        {
            throw new ZstdException("zstd frame content size mismatch.");
        }

        return pos;
    }

    // ------------------------------------------------------------------
    // Compressed blocks
    // ------------------------------------------------------------------

    private static void DecompressBlock(
        byte[] src, int offset, int length,
        List<byte> output, int frameStart, FrameContext ctx, ulong maxBlock)
    {
        int end = offset + length;
        int pos = offset;

        // ---- Literals section ----
        if (pos >= end)
        {
            throw new ZstdException("Truncated literals header.");
        }

        int litType = src[pos] & 3;
        byte[] literals;
        if (litType == 0 || litType == 1)
        {
            int sizeFormat = (src[pos] >> 2) & 3;
            int regen;
            if (sizeFormat == 0 || sizeFormat == 2)
            {
                regen = src[pos] >> 3;
                pos += 1;
            }
            else if (sizeFormat == 1)
            {
                if (pos + 2 > end)
                {
                    throw new ZstdException("Truncated literals header.");
                }

                regen = (src[pos] >> 4) + (src[pos + 1] << 4);
                pos += 2;
            }
            else
            {
                if (pos + 3 > end)
                {
                    throw new ZstdException("Truncated literals header.");
                }

                regen = (src[pos] >> 4) + (src[pos + 1] << 4) + (src[pos + 2] << 12);
                pos += 3;
            }

            if ((ulong)regen > maxBlock)
            {
                throw new ZstdException("Literals size exceeds block maximum.");
            }

            if (litType == 0)
            {
                if (pos + regen > end)
                {
                    throw new ZstdException("Truncated raw literals.");
                }

                literals = new byte[regen];
                Array.Copy(src, pos, literals, 0, regen);
                pos += regen;
            }
            else
            {
                if (pos + 1 > end)
                {
                    throw new ZstdException("Truncated RLE literals.");
                }

                literals = new byte[regen];
                Array.Fill(literals, src[pos]);
                pos += 1;
            }
        }
        else
        {
            bool isCompressed = litType == 2;
            int sizeFormat = (src[pos] >> 2) & 3;
            int headerSize = sizeFormat switch { 0 => 3, 1 => 3, 2 => 4, _ => 5 };
            if (pos + headerSize > end)
            {
                throw new ZstdException("Truncated literals header.");
            }

            ulong bits = 0;
            for (int i = 0; i < headerSize; i++)
            {
                bits |= (ulong)src[pos + i] << (i * 8);
            }

            int regen, compSize;
            bool fourStreams;
            if (sizeFormat is 0 or 1)
            {
                regen = (int)((bits >> 4) & 0x3FF);
                compSize = (int)((bits >> 14) & 0x3FF);
                fourStreams = sizeFormat == 1;
            }
            else if (sizeFormat == 2)
            {
                regen = (int)((bits >> 4) & 0x3FFF);
                compSize = (int)((bits >> 18) & 0x3FFF);
                fourStreams = true;
            }
            else
            {
                regen = (int)((bits >> 4) & 0x3FFFF);
                compSize = (int)((bits >> 22) & 0x3FFFF);
                fourStreams = true;
            }

            pos += headerSize;
            if ((ulong)regen > maxBlock)
            {
                throw new ZstdException("Literals size exceeds block maximum.");
            }

            if (compSize <= 0 || pos + compSize > end)
            {
                throw new ZstdException("Truncated Huffman literals.");
            }

            ZstdHuffman.HuffmanTable huffman;
            int streamsOffset = pos;
            int streamsLength = compSize;
            if (isCompressed)
            {
                int treeSize = ZstdHuffman.ReadStats(
                    src, pos, compSize,
                    out byte[] weights, out int tableLog, out int numSymbols);
                huffman = ZstdHuffman.BuildTable(weights, numSymbols, tableLog);
                ctx.HuffmanTable = huffman;
                streamsOffset += treeSize;
                streamsLength -= treeSize;
                if (streamsLength <= 0)
                {
                    throw new ZstdException("Truncated Huffman streams.");
                }
            }
            else
            {
                huffman = ctx.HuffmanTable ??
                          throw new ZstdException("Treeless literals without a Huffman table.");
            }

            literals = new byte[regen];
            if (!fourStreams)
            {
                ZstdHuffman.DecodeStream(
                    src, streamsOffset, streamsLength,
                    huffman, literals, 0, regen);
            }
            else
            {
                if (regen < 6)
                {
                    throw new ZstdException("Invalid 4-stream literals size.");
                }

                if (streamsLength < 10)
                {
                    throw new ZstdException("Truncated Huffman jump table.");
                }

                int s1 = ReadU16LE(src, streamsOffset);
                int s2 = ReadU16LE(src, streamsOffset + 2);
                int s3 = ReadU16LE(src, streamsOffset + 4);
                int s4 = streamsLength - 6 - s1 - s2 - s3;
                if (s4 < 0)
                {
                    throw new ZstdException("Invalid Huffman jump table.");
                }

                int seg = (regen + 3) / 4;
                if ((long)seg * 3 > regen)
                {
                    throw new ZstdException("Invalid Huffman stream split.");
                }

                int d1 = 0, d2 = seg, d3 = 2 * seg, d4 = 3 * seg;
                int l1 = seg, l2 = seg, l3 = seg, l4 = regen - 3 * seg;
                int c1 = streamsOffset + 6, c2 = c1 + s1, c3 = c2 + s2, c4 = c3 + s3;
                ZstdHuffman.DecodeStream(src, c1, s1, huffman, literals, d1, l1);
                ZstdHuffman.DecodeStream(src, c2, s2, huffman, literals, d2, l2);
                ZstdHuffman.DecodeStream(src, c3, s3, huffman, literals, d3, l3);
                ZstdHuffman.DecodeStream(src, c4, s4, huffman, literals, d4, l4);
            }

            pos += compSize;
        }

        // ---- Sequences section ----
        int seqSize = end - pos;
        if (seqSize <= 0)
        {
            throw new ZstdException("Missing sequences section.");
        }

        int blockOutStart = output.Count;
        int maxOut = blockOutStart + (int)maxBlock;

        int numSeq = src[pos++];
        if (numSeq == 0)
        {
            if (pos != end)
            {
                throw new ZstdException("Extraneous data in sequences section.");
            }
        }
        else
        {
            if (numSeq >= 128)
            {
                if (numSeq == 255)
                {
                    if (pos + 2 > end)
                    {
                        throw new ZstdException("Truncated sequence count.");
                    }

                    numSeq = ReadU16LE(src, pos) + 0x7F00;
                    pos += 2;
                }
                else
                {
                    if (pos + 1 > end)
                    {
                        throw new ZstdException("Truncated sequence count.");
                    }

                    numSeq = ((numSeq - 128) << 8) + src[pos++];
                }
            }

            if (pos >= end)
            {
                throw new ZstdException("Truncated sequence modes.");
            }

            byte modes = src[pos++];
            if ((modes & 3) != 0)
            {
                throw new ZstdException("Reserved sequence mode bits set.");
            }

            int llMode = (modes >> 6) & 3;
            int ofMode = (modes >> 4) & 3;
            int mlMode = (modes >> 2) & 3;

            pos = BuildSeqTableForMode(src, pos, end, llMode, MaxLL, 9, LLBase, LLBits, LLDefaultTable,
                ref ctx.LLTable);
            pos = BuildOFTableForMode(src, pos, end, ofMode, ref ctx.OFTable);
            pos = BuildSeqTableForMode(src, pos, end, mlMode, MaxML, 9, MLBase, MLBits, MLDefaultTable,
                ref ctx.MLTable);

            DecodeSequences(src, pos, end, numSeq, literals, output, maxOut, frameStart, ctx);
        }

        // Trailing literals (or all of them when numSeq == 0).
        // Execution for numSeq > 0 already appended its literals; append rest.
        if (numSeq == 0)
        {
            if (output.Count + literals.Length > maxOut)
            {
                throw new ZstdException("Block output exceeds maximum.");
            }

            output.AddRange(literals);
        }

        if (output.Count - blockOutStart > (int)maxBlock)
        {
            throw new ZstdException("Block output exceeds maximum.");
        }
    }

    private static int BuildSeqTableForMode(
        byte[] src, int pos, int end, int mode, int maxSymbol, int maxLog,
        uint[] bases, byte[] extras, SeqTable defaultTable, ref SeqTable? current)
    {
        switch (mode)
        {
            case 0: // predefined
                current = defaultTable;
                return pos;
            case 1: // RLE
                if (pos >= end)
                {
                    throw new ZstdException("Truncated RLE sequence table.");
                }

                int symbol = src[pos++];
                if (symbol > maxSymbol)
                {
                    throw new ZstdException("Invalid RLE sequence symbol.");
                }

                current = BuildRleSeqTable(bases[symbol], extras[symbol]);
                return pos;
            case 2: // FSE-compressed
            {
                int consumed = ZstdFse.ParseNormalizedCounts(
                    src, pos, end - pos, maxSymbol,
                    out short[] norms, out int tableLog, out int maxSym);
                if (tableLog > maxLog)
                {
                    throw new ZstdException("Sequence tableLog too large.");
                }

                current = BuildSeqTable(norms, maxSym, tableLog, bases, extras);
                return pos + consumed;
            }

            case 3: // repeat
                if (current is null)
                {
                    throw new ZstdException("Repeat sequence table without previous table.");
                }

                return pos;
            default:
                throw new ZstdException("Invalid sequence mode.");
        }
    }

    private static int BuildOFTableForMode(
        byte[] src, int pos, int end, int mode, ref SeqTable? current)
    {
        switch (mode)
        {
            case 0:
                current = OFDefaultTable;
                return pos;
            case 1:
                if (pos >= end)
                {
                    throw new ZstdException("Truncated RLE sequence table.");
                }

                int symbol = src[pos++];
                if (symbol > MaxOff)
                {
                    throw new ZstdException("Invalid RLE sequence symbol.");
                }

                current = BuildRleSeqTable((uint)OffsetBase(symbol), (byte)symbol);
                return pos;
            case 2:
            {
                int consumed = ZstdFse.ParseNormalizedCounts(
                    src, pos, end - pos, MaxOff,
                    out short[] norms, out int tableLog, out int maxSym);
                if (tableLog > 8)
                {
                    throw new ZstdException("Sequence tableLog too large.");
                }

                current = BuildOFTable(norms, maxSym, tableLog);
                return pos + consumed;
            }

            case 3:
                if (current is null)
                {
                    throw new ZstdException("Repeat sequence table without previous table.");
                }

                return pos;
            default:
                throw new ZstdException("Invalid sequence mode.");
        }
    }

    private static void DecodeSequences(
        byte[] src, int offset, int end, int numSeq,
        byte[] literals, List<byte> output, int maxOut, int frameStart, FrameContext ctx)
    {
        var bitD = BackwardBitReader.ForSequenceStream(src, offset, end - offset);

        SeqTable llTable = ctx.LLTable!;
        SeqTable ofTable = ctx.OFTable!;
        SeqTable mlTable = ctx.MLTable!;

        int llState = (int)bitD.ReadBits(llTable.TableLog);
        int ofState = (int)bitD.ReadBits(ofTable.TableLog);
        int mlState = (int)bitD.ReadBits(mlTable.TableLog);

        ulong[] rep = ctx.RepeatOffsets;
        int litPos = 0;

        for (int i = 0; i < numSeq; i++)
        {
            bool isLast = i == numSeq - 1;
            SeqEntry ll = GetEntry(llTable, llState);
            SeqEntry ml = GetEntry(mlTable, mlState);
            SeqEntry of = GetEntry(ofTable, ofState);

            // Offset first (reference order).
            ulong dist;
            if (of.ExtraBits > 1)
            {
                dist = of.Baseline + bitD.ReadBits(of.ExtraBits);
                rep[2] = rep[1];
                rep[1] = rep[0];
                rep[0] = dist;
            }
            else
            {
                int ll0 = ll.Baseline == 0 ? 1 : 0;
                if (of.ExtraBits == 0)
                {
                    ulong temp = rep[ll0];
                    rep[1] = rep[ll0 ^ 1];
                    rep[0] = temp;
                    dist = temp;
                    if (dist == 0)
                    {
                        throw new ZstdException("Invalid repeat offset.");
                    }
                }
                else
                {
                    uint bit = bitD.ReadBits(1);
                    ulong index = of.Baseline + (ulong)ll0 + bit; // 1..3
                    ulong temp = index == 3
                        ? (rep[0] == 0 ? throw new ZstdException("Invalid repeat offset.") : rep[0] - 1)
                        : rep[index];
                    if (temp == 0)
                    {
                        throw new ZstdException("Invalid repeat offset.");
                    }

                    if (index != 1)
                    {
                        rep[2] = rep[1];
                    }

                    rep[1] = rep[0];
                    rep[0] = temp;
                    dist = temp;
                }
            }

            uint matchLen = ml.Baseline + (ml.ExtraBits > 0 ? bitD.ReadBits(ml.ExtraBits) : 0);
            uint litLen = ll.Baseline + (ll.ExtraBits > 0 ? bitD.ReadBits(ll.ExtraBits) : 0);

            if (!isLast)
            {
                llState = ll.NextState + (int)bitD.ReadBits(ll.NumBits);
                mlState = ml.NextState + (int)bitD.ReadBits(ml.NumBits);
                ofState = of.NextState + (int)bitD.ReadBits(of.NumBits);
            }

            ExecuteSequence(literals, ref litPos, output, maxOut, frameStart, ctx.WindowSize, litLen, matchLen, dist);
        }

        if (!bitD.IsAtEnd)
        {
            throw new ZstdException("Sequence bitstream not exactly consumed.");
        }

        // Trailing literals.
        int remaining = literals.Length - litPos;
        if (remaining < 0)
        {
            throw new ZstdException("Literals over-consumed.");
        }

        if (output.Count + remaining > maxOut)
        {
            throw new ZstdException("Block output exceeds maximum.");
        }

        for (int i = 0; i < remaining; i++)
        {
            output.Add(literals[litPos + i]);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct SeqEntry
    {
        public readonly uint Baseline;
        public readonly int ExtraBits;
        public readonly int NumBits;
        public readonly int NextState;

        public SeqEntry(uint baseline, int extraBits, int numBits, int nextState)
        {
            Baseline = baseline;
            ExtraBits = extraBits;
            NumBits = numBits;
            NextState = nextState;
        }
    }

    private static SeqEntry GetEntry(SeqTable table, int state) => new(
        table.Bases[state], table.ExtraBits[state], table.NumBits[state], table.NewState[state]);

    private static void ExecuteSequence(
        byte[] literals, ref int litPos, List<byte> output, int maxOut, int frameStart,
        ulong windowSize, uint litLen, uint matchLen, ulong dist)
    {
        if (litPos + (long)litLen > literals.Length)
        {
            throw new ZstdException("Literals over-consumed.");
        }

        if (output.Count + (long)litLen + matchLen > maxOut)
        {
            throw new ZstdException("Block output exceeds maximum.");
        }

        for (uint i = 0; i < litLen; i++)
        {
            output.Add(literals[litPos + i]);
        }

        litPos += (int)litLen;

        long frameOut = (long)output.Count - frameStart;
        if (dist == 0 || dist > (ulong)frameOut || dist > windowSize)
        {
            throw new ZstdException("Invalid match offset.");
        }

        int matchPos = output.Count - (int)dist;
        for (uint i = 0; i < matchLen; i++)
        {
            output.Add(output[matchPos + (int)i]);
        }
    }

    // ------------------------------------------------------------------
    // Little-endian helpers
    // ------------------------------------------------------------------

    private static uint ReadU32LE(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static int ReadU16LE(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8);

    private static ulong ReadUIntLE(byte[] buf, int offset, int size)
    {
        ulong value = 0;
        for (int i = 0; i < size; i++)
        {
            value |= (ulong)buf[offset + i] << (i * 8);
        }

        return value;
    }
}

/// <summary>
/// Decoder resource limits for <see cref="ZstdDecompressor"/>.
/// ZArchive blocks need only 64 KiB windows; the defaults accept foreign
/// frames up to 512 MiB. Lower the caps to harden untrusted-input paths.
/// </summary>
public sealed class ZstdDecoderOptions
{
    /// <summary>Default limits: 512 MiB window, 512 MiB frame content.</summary>
    public static ZstdDecoderOptions Default { get; } = new();

    /// <summary>
    /// Maximum accepted window size in bytes (default 512 MiB).
    /// Frames declaring a larger window are rejected before decoding.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When set to zero.</exception>
    public ulong MaxWindowSize
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "MaxWindowSize must be positive.");
    } = 512UL * 1024 * 1024;

    /// <summary>
    /// Maximum accepted frame content size in bytes (default 512 MiB).
    /// Bounds allocation for frames without a declared content size.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When set to zero.</exception>
    public ulong MaxFrameContentSize
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "MaxFrameContentSize must be positive.");
    } = 512UL * 1024 * 1024;
}