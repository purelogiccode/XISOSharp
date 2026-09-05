namespace ZARSharp.Zstd;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// zstd strategy selector. Exact names from <c>lib/zstd.h</c>
/// (<c>ZSTD_strategy</c>); parameters per level come from
/// <see cref="ZstdCompressionParameters"/> (port of <c>clevels.h</c>).
/// All strategies are implemented across levels 1..22 (fast, double-fast,
/// greedy, lazy, lazy2, btlazy2, btopt, btultra, btultra2).
/// </summary>
public enum ZstdStrategy
{
    /// <summary>Level 1 (zstd_fast.c).</summary>
    Fast = 1,

    /// <summary>Double-fast (zstd_double_fast.c).</summary>
    DoubleFast = 2,

    /// <summary>Greedy (zstd_lazy.c depth 0).</summary>
    Greedy = 3,

    /// <summary>Lazy (zstd_lazy.c depth 1).</summary>
    Lazy = 4,

    /// <summary>Lazy2 (zstd_lazy.c depth 2).</summary>
    Lazy2 = 5,

    /// <summary>Binary-tree lazy2 (zstd_opt.c, bt).</summary>
    BtLazy2 = 6,

    /// <summary>Binary-tree optimal (zstd_opt.c).</summary>
    BtOpt = 7,

    /// <summary>Binary-tree ultra (zstd_opt.c).</summary>
    BtUltra = 8,

    /// <summary>Binary-tree ultra2 (zstd_opt.c).</summary>
    BtUltra2 = 9,
}

/// <summary>
/// Compression options. Levels 1..22 (full <c>ZSTD_MAX_CLEVEL</c> range).
/// ZAR blocks are always 64 KiB, so the <c>clevels.h</c> row for
/// <c>srcSize &lt;= 128 KiB</c> applies; see
/// <see cref="ZstdCompressionParameters"/>.
/// </summary>
public sealed class ZstdCompressionOptions
{
    /// <summary>Compression level 1..22 (default 6, matching upstream).</summary>
    public int Level { get; init; } = 6;

    /// <summary>Write a 4-byte XXH64 content checksum (default false, matching upstream).</summary>
    public bool ChecksumFlag { get; init; }

    /// <summary>Creates options for <paramref name="level"/> (1..22).</summary>
    public static ZstdCompressionOptions FromLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        return new ZstdCompressionOptions { Level = level };
    }
}

/// <summary>
/// Pure-C# zstd compressor: single-shot frames of native 128 KiB blocks
/// (<c>ZSTD_BLOCKSIZE_MAX</c>, smaller when the adjusted window is smaller),
/// with frame-level parameters shared by every block and persistent
/// frame-scoped repeat-offset history (RFC 8878 §4.1.1). C# port of
/// <c>ZSTD_writeFrameHeader</c> / <c>ZSTD_compress_frameChunk</c> framing
/// (<c>lib/compress/zstd_compress.c</c>, RFC 8878 §3) over
/// <see cref="ZstdBlockEncoder"/> blocks. Default level 6, matching the
/// upstream <c>ZSTD_compress(..., 6)</c> call in <c>StoreBlock</c>.
/// </summary>
public sealed class ZstdCompressor : IZarBlockCompressor
{
    private const uint FrameMagic = 0xFD2FB528;

    /// <summary>
    /// Maximum block size (<c>ZSTD_BLOCKSIZE_MAX == 1 &lt;&lt; ZSTD_BLOCKSIZELOG_MAX</c>).
    /// </summary>
    private const int MaxBlockSize = 131072;

    /// <summary>
    /// Blocks below this size go raw without attempting compression
    /// (<c>ZSTD_buildSeqStore</c>: <c>srcSize &lt; MIN_CBLOCK_SIZE +
    /// ZSTD_blockHeaderSize + 1 + 1</c>, with <c>MIN_CBLOCK_SIZE == 2</c>).
    /// </summary>
    private const int MinCompressibleBlock = 2 + 3 + 1 + 1;

    /// <summary>
    /// Entropy payloads below this size on uniform non-first blocks emit
    /// <c>bt_rle</c> (<c>rleMaxLength</c> in <c>ZSTD_compressBlock_internal</c>).
    /// </summary>
    internal const int RleMaxLength = 25;

    /// <summary>Creates a compressor (default level 6).</summary>
    public ZstdCompressor(ZstdCompressionOptions? options = null)
    {
        Options = options ?? new ZstdCompressionOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.Level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Options.Level, 22);
    }

    /// <summary>Options in effect.</summary>
    public ZstdCompressionOptions Options { get; }

    /// <summary>
    /// Compresses <paramref name="source"/> as a single-shot frame into
    /// <paramref name="destination"/>. Returns the frame size, or -1 when the
    /// frame would not fit or would not be smaller than the input (the caller
    /// then stores raw — same rule as <c>StoreBlock</c>).
    /// </summary>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var frame = EncodeFrame(source, Options.Level, Options.ChecksumFlag);
        if (frame.Length >= source.Length || frame.Length > destination.Length)
        {
            return -1;
        }

        frame.CopyTo(destination);
        return frame.Length;
    }

    /// <summary>Matches <c>ZSTD_compressBound</c> for single-shot frames.</summary>
    public static int GetCompressBound(int sourceSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSize);

        // lib/zstd.h: ZSTD_COMPRESSBOUND(s) = s + (s>>8) + (s<128KB ? (128KB-s)>>11 : 0).
        var bound = (long)sourceSize + (sourceSize >> 8);
        if (sourceSize < 131072)
        {
            bound += (131072 - sourceSize) >> 11;
        }

        if (bound > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSize));
        }

        return (int)bound;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into a single self-contained zstd
    /// frame (always valid; incompressible chunks become raw blocks inside the
    /// frame, so unlike <see cref="Compress"/> this never declines).
    /// Decodable by <see cref="DecompressFrame"/>, the C# decoder, and native
    /// zstd.
    /// </summary>
    public byte[] CompressBlock(ReadOnlySpan<byte> source)
    {
        return EncodeFrame(source, Options.Level, Options.ChecksumFlag);
    }

    /// <summary>Thin wrapper over the existing decoder ( Phase-0 harness convenience).</summary>
    public static byte[] DecompressFrame(ReadOnlySpan<byte> src, int maxSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSize);
        var copy = src.ToArray();
        var full = ZstdDecompressor.Decompress(copy);
        if (full.Length > maxSize)
        {
            throw new ZstdException("Decompressed size exceeds maximum.");
        }

        return full;
    }

    // ------------------------------------------------------------------
    // Frame writer (ZSTD_writeFrameHeader / ZSTD_compress_frameChunk,
    // single-shot, no dict, no target block size)
    // ------------------------------------------------------------------

    private static byte[] EncodeFrame(ReadOnlySpan<byte> src, int level, bool checksum)
    {
        // Single-shot cParams from the TOTAL size (ZSTD_getCParams +
        // ZSTD_adjustCParams_internal), shared by the frame header and every
        // block — never re-resolved per block.
        var prm = ZstdCompressionParameters.ForSizeAndLevel(src.Length, level).AdjustForSize(src.Length);

        // ZSTD_resetCCtx_internal: windowSize = MAX(1, MIN(1<<windowLog,
        // pledgedSrcSize)); blockSize = MIN(maxBlockSize, windowSize).
        var windowSize = Math.Max(1L, Math.Min(1L << prm.WindowLog, (long)src.Length));
        var blockMax = (int)Math.Min(MaxBlockSize, windowSize);

        var dst = new byte[GetCompressBound(src.Length)];
        var pos = WriteFrameHeader(dst, 0, src.Length, checksum, level);

        // Persistent match state (M2) for every strategy: the state holds the
        // frame copy the engines index absolutely (copied only when
        // stateful). Single-shot inputs below two blocks never touch it
        // beyond the first block, so behavior there is unchanged.
        var stateful = prm.Strategy
            is ZstdStrategy.Fast or ZstdStrategy.DoubleFast
            or ZstdStrategy.Greedy or ZstdStrategy.Lazy or ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2
            or ZstdStrategy.BtOpt or ZstdStrategy.BtUltra or ZstdStrategy.BtUltra2;
        var frame = stateful ? src.ToArray() : [];
        ZstdFrameState? state = stateful ? new ZstdFrameState(frame, level, prm) : null;

        // Post-block splitter for optimal-parser strategies with a big
        // window (M4: ZSTD_resolveBlockSplitterMode).
        var splitBlocks = state is not null && ZstdBlockSplitter.Enabled(prm);

        // Repeat-offset history is frame-scoped (RFC 8878 §4.1.1): initialized
        // once and carried across blocks. The decoder leaves its history
        // untouched for raw/RLE blocks, so the staged history is confirmed
        // only for emitted compressed blocks (upstream runs
        // ZSTD_blockState_confirmRepcodesAndEntropyTables only for those).
        // Empty input still emits one empty last block — a frame with zero
        // blocks would not decode.
        var rep = ZstdSeq.FreshRepeatOffsets();
        var remaining = src.Length;
        var inPos = 0;
        var isFirstBlock = true;
        // Running consumed-minus-produced balance: splitting past the first
        // full block needs verified savings (ZSTD_compress_frameChunk).
        long savings = 0;
        var frameSpan = state is not null
            ? new ReadOnlySpan<byte>(frame)
            : src;
        do
        {
            var chunk = ZstdBlockSplitter.OptimalBlockSize(
                frameSpan, inPos, remaining, blockMax, prm.Strategy, ref savings);
            var last = chunk == remaining;
            var chunkBytes = state is not null
                ? new ReadOnlySpan<byte>(frame, inPos, chunk)
                : src.Slice(inPos, chunk);
            int written;
            if (splitBlocks)
            {
                written = ZstdBlockSplitter.WriteSplitBlock(
                    state!, inPos, chunkBytes, dst, pos, dst.Length - pos,
                    last, rep, isFirstBlock);
                isFirstBlock = false;
            }
            else
            {
                written = WriteFrameBlock(
                    chunkBytes, inPos, level, prm,
                    dst, pos, dst.Length - pos, last, rep, ref isFirstBlock, state);
            }

            savings += (long)chunk - written;
            pos += written;
            inPos += chunk;
            remaining -= chunk;
        } while (remaining > 0);

        if (checksum)
        {
            var csum = (uint)ZstdXxh64.Hash64(src.ToArray(), 0, src.Length);
            if (pos + 4 > dst.Length)
            {
                throw new ZstdException("Frame destination too small.");
            }

            dst[pos] = (byte)csum;
            dst[pos + 1] = (byte)(csum >> 8);
            dst[pos + 2] = (byte)(csum >> 16);
            dst[pos + 3] = (byte)(csum >> 24);
            pos += 4;
        }

        Array.Resize(ref dst, pos);
        return dst;
    }

    /// <summary>
    /// Writes one frame block (header included) at <c>dst[pos]</c>; returns
    /// the bytes written. Exact port of the plain (no splitter, no target
    /// size) arm of <c>ZSTD_compress_frameChunk</c> over
    /// <c>ZSTD_compressBlock_internal</c>: tiny blocks go raw directly
    /// (<c>ZSTDbss_noCompress</c>), otherwise the entropy payload must beat
    /// <c>blockSize - ZSTD_minGain</c> (else raw), and a tiny payload on a
    /// uniform non-first block emits <c>bt_rle</c> (the first block never
    /// does, for pre-1.4.3 decoder compatibility). Always fits: the bound's
    /// slack covers headers plus one raw block per chunk.
    /// </summary>
    private static int WriteFrameBlock(
        ReadOnlySpan<byte> chunk, int blockStart, int level, ZstdCompressionParameters prm,
        byte[] dst, int pos, int capacity, bool last, uint[] rep, ref bool isFirstBlock,
        ZstdFrameState? state)
    {
        if (chunk.Length < MinCompressibleBlock)
        {
            // Tiny blocks go raw without parsing; the offset-code downgrade
            // still applies (native reaches the same tail past ZSTDbss_noCompress).
            state?.DeclineEntropy();
            WriteRawBlock(chunk, dst, pos, last);
            isFirstBlock = false;
            return 3 + chunk.Length;
        }

        var r0 = rep[0];
        var r1 = rep[1];
        var r2 = rep[2];
        int payload;
        try
        {
            payload = state is not null
                ? ZstdBlockEncoder.EncodeBlockPayloadStateful(
                    state, blockStart, blockStart + chunk.Length, dst, pos + 3, capacity - 3, rep)
                : ZstdBlockEncoder.EncodeBlockPayload(
                    chunk, level, prm, dst, pos + 3, capacity - 3, rep);
        }
        catch (ZstdException)
        {
            payload = -1;
        }

        var maxCSize = chunk.Length - ZstdBlockEncoder.MinGain(chunk.Length, prm.Strategy);
        int written;
        if (payload < 0 || payload >= maxCSize || payload > (1 << 21) - 1)
        {
            // Raw block (ZSTD_noCompressBlock): 3-byte header + copy.
            rep[0] = r0;
            rep[1] = r1;
            rep[2] = r2;
            state?.DeclineEntropy();
            WriteRawBlock(chunk, dst, pos, last);
            written = 3 + chunk.Length;
        }
        else if (!isFirstBlock && payload < RleMaxLength && IsUniform(chunk))
        {
            // RLE block (ZSTD_rleCompressBlock): header carries the
            // decompressed size, payload is the single repeated byte. Like
            // raw, it resets the decoder-side history: un-confirm staged rep.
            rep[0] = r0;
            rep[1] = r1;
            rep[2] = r2;
            state?.DeclineEntropy();
            if (capacity < 4)
            {
                throw new ZstdException("Frame destination too small.");
            }

            var header = (last ? 1u : 0u) | (1u << 1) | ((uint)chunk.Length << 3); // Type RLE(1).
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
            dst[pos + 3] = chunk[0];
            written = 4;
        }
        else
        {
            state?.ConfirmEntropy();
            var header = (last ? 1u : 0u) | (2u << 1) | ((uint)payload << 3); // Type compressed(2).
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
            written = 3 + payload;
        }

        isFirstBlock = false;
        return written;
    }

    private static void WriteRawBlock(ReadOnlySpan<byte> chunk, byte[] dst, int pos, bool last)
    {
        if (pos + 3 + chunk.Length > dst.Length)
        {
            throw new ZstdException("Frame destination too small.");
        }

        var header = (last ? 1u : 0u) | ((uint)chunk.Length << 3); // Type raw(0).
        dst[pos] = (byte)header;
        dst[pos + 1] = (byte)(header >> 8);
        dst[pos + 2] = (byte)(header >> 16);
        chunk.CopyTo(new Span<byte>(dst, pos + 3, chunk.Length));
    }

    /// <summary><c>ZSTD_isRLE</c>: every byte identical (never called empty here).</summary>
    internal static bool IsUniform(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0)
        {
            return false;
        }

        var first = chunk[0];
        for (var i = 1; i < chunk.Length; i++)
        {
            if (chunk[i] != first)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes magic + descriptor + optional window descriptor + frame content
    /// size. Exact port of <c>ZSTD_writeFrameHeader</c>
    /// (<c>lib/compress/zstd_compress.c</c>) for the single-shot no-dict case:
    /// <c>windowLog</c> is the <em>adjusted</em> log
    /// (<see cref="ZstdCompressionParameters.AdjustForSize"/>),
    /// <c>singleSegment</c> omits the window byte when the window covers the
    /// content, and the FCS width follows the 256 / 65792 / 2³² thresholds.
    /// Returns the header length.
    /// </summary>
    private static int WriteFrameHeader(byte[] dst, int offset, int contentSize, bool checksum, int level)
    {
        var applied = ZstdCompressionParameters.ForSizeAndLevel(contentSize, level).AdjustForSize(contentSize);
        return WriteFrameHeader(applied.WindowLog, dst, offset, contentSize, checksum);
    }

    internal static int WriteFrameHeader(int windowLog, byte[] dst, int offset, int contentSize, bool checksum)
    {
        var content = (long)contentSize;
        var windowSize = 1L << windowLog;
        var singleSegment = windowSize >= content ? 1 : 0;
        var fcsCode = (content >= 256 ? 1 : 0) + (content >= 65792 ? 1 : 0) + (content >= 0xFFFFFFFFL ? 1 : 0);
        dst[offset] = (byte)(FrameMagic & 0xFF);
        dst[offset + 1] = (byte)((FrameMagic >> 8) & 0xFF);
        dst[offset + 2] = (byte)((FrameMagic >> 16) & 0xFF);
        dst[offset + 3] = (byte)((FrameMagic >> 24) & 0xFF);
        dst[offset + 4] = (byte)((checksum ? 0x04 : 0) | (singleSegment << 5) | (fcsCode << 6));
        var pos = offset + 5;
        if (singleSegment == 0)
        {
            dst[pos++] = (byte)((windowLog - 10) << 3);
        }

        switch (fcsCode)
        {
            case 0:
                if (singleSegment == 1)
                {
                    dst[pos++] = (byte)content;
                }

                break;
            case 1:
                var biased = content - 256;
                dst[pos++] = (byte)biased;
                dst[pos++] = (byte)(biased >> 8);
                break;
            case 2:
                dst[pos++] = (byte)content;
                dst[pos++] = (byte)(content >> 8);
                dst[pos++] = (byte)(content >> 16);
                dst[pos++] = (byte)(content >> 24);
                break;
            default:
                dst[pos++] = (byte)content;
                dst[pos++] = (byte)(content >> 8);
                dst[pos++] = (byte)(content >> 16);
                dst[pos++] = (byte)(content >> 24);
                dst[pos++] = (byte)(content >> 32);
                dst[pos++] = (byte)(content >> 40);
                dst[pos++] = (byte)(content >> 48);
                dst[pos++] = (byte)(content >> 56);
                break;
        }

        return pos - offset;
    }
}