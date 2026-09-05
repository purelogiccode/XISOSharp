using System.Numerics;

namespace ZARSharp.Zstd;

/// <summary>
/// Block encoder: literals section + sequences section + 3-byte block header.
/// C# port of <c>lib/compress/zstd_compress_literals.c</c>
/// (<c>ZSTD_compressLiterals</c>), <c>lib/compress/zstd_compress_sequences.c</c>
/// (<c>ZSTD_buildCTable</c> / <c>ZSTD_encodeSequences_body</c>), the code mapping
/// (<c>ZSTD_LLcode</c> / <c>ZSTD_MLcode</c> / <c>ZSTD_highbit32(offBase)</c> from
/// <c>lib/compress/zstd_compress_internal.h:584-613</c> and
/// <c>lib/compress/zstd_compress.c:2693-2719</c>), and the nbSeq header from
/// <c>lib/compress/zstd_compress_superblock.c</c>. Emits one compressed block
/// per call (RFC 8878 §4); the frame writer (<c>ZstdCompressor</c>, Phase 6)
/// adds the frame header and decides the raw fallback.
/// <para/>
/// Deliberate simplifications (validity-preserving): tables are written fresh
/// per block, so <c>set_repeat</c> (sequence tables) and treeless literals
/// never occur; single-shot fresh contexts have no previous tables.
/// <c>set_basic</c> is selected exactly like
/// <c>ZSTD_selectEncodingType</c> (single-symbol ≤2 → basic, else
/// basic-vs-compressed cost); <c>longOffsets</c> (offset codes ≥ 32,
/// impossible with windowLog ≤ 17) declines instead of emitting a wrong stream.
/// </summary>
internal static class ZstdBlockEncoder
{
    internal const int BlockHeaderSize = 3;
    private const int BlockTypeCompressed = 2;
    private const int MaxBlockPayload = (1 << 21) - 1;

    // Sequence-alphabet accuracy logs (LLFSELog / MLFSELog / OffFSELog):
    // also the decoder's acceptance caps (ZstdDecompressor rereads them).
    internal const int LlFseLog = 9;
    internal const int MlFseLog = 9;
    internal const int OffFseLog = 8;

    internal const int MaxLl = 35;
    internal const int MaxMl = 52;
    internal const int MaxOff = 31;

    // Literals-section mode tags (shared with the raw/RLE header forms).
    private const uint SetBasic = 0; // Raw literals.
    private const uint SetRle = 1; // RLE literals.
    private const uint SetCompressed = 2; // Huffman-compressed literals.
    private const uint SetRepeat = 3; // Treeless (previous-table) literals.

    // Sequence-section mode tags (2 bits per alphabet in the modes byte).
    internal const int SeqModeBasic = 0;
    internal const int SeqModeRle = 1;
    internal const int SeqModeFse = 2;
    internal const int SeqModeRepeat = 3;

    internal const int DefaultMaxOff = 28;
    internal const int LlDefaultNormLog = 6;
    internal const int MlDefaultNormLog = 6;
    internal const int OfDefaultNormLog = 5;

    internal static readonly short[] LlDefaultNorm =
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    ];

    internal static readonly short[] MlDefaultNorm =
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    ];

    internal static readonly short[] OfDefaultNorm =
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ];

    // nbSeq long form threshold (LONGNBSEQ).
    internal const int LongNbSeq = 0x7F00;

    // ZSTD_LLcode table (lib/compress/zstd_compress_internal.h:586-593).
    private static readonly byte[] LlCodeTable =
    [
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
        16, 16, 17, 17, 18, 18, 19, 19,
        20, 20, 20, 20, 21, 21, 21, 21,
        22, 22, 22, 22, 22, 22, 22, 22,
        23, 23, 23, 23, 23, 23, 23, 23,
        24, 24, 24, 24, 24, 24, 24, 24,
        24, 24, 24, 24, 24, 24, 24, 24,
    ];

    // ZSTD_MLcode table (lib/compress/zstd_compress_internal.h:603-610).
    private static readonly byte[] MlCodeTable =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 32, 33, 33, 34, 34, 35, 35, 36, 36, 36, 36, 37, 37, 37, 37,
        38, 38, 38, 38, 38, 38, 38, 38, 39, 39, 39, 39, 39, 39, 39, 39,
        40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40,
        41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41,
        42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42,
        42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42, 42,
    ];

    // Sequence baseline / extra-bit tables (RFC 8878 §4.1.1; same values as
    // the ZstdDecompressor LLBase/LLBits/MLBase/MLBits tables they invert).
    private static readonly byte[] LlBits =    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    private static readonly byte[] MlBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    /// <summary>
    /// Encodes <paramref name="src"/> as one standalone block (literals section +
    /// sequences section) with a 3-byte header, using the match finder for
    /// <paramref name="level"/> (1..22; every strategy is implemented).
    /// Repeat history starts fresh
    /// (<c>{1, 4, 8}</c>), which is correct only for the first (or only) block
    /// of a frame. Returns total bytes written including the header, or -1 when
    /// the block does not fit (<paramref name="dstCapacity"/> too small) or needs
    /// the unimplemented long-offsets path — the caller then stores raw instead
    /// (mirrors C's <c>dstSize_tooSmall</c> → <c>ZSTD_noCompressBlock</c> fallback).
    /// </summary>
    public static int EncodeBlock(
        ReadOnlySpan<byte> src, int level,
        byte[] dst, int dstOffset, int dstCapacity, bool lastBlock)
    {
        return EncodeBlock(src, level, dst, dstOffset, dstCapacity, lastBlock, ZstdSeq.FreshRepeatOffsets());
    }

    /// <summary>
    /// Encodes <paramref name="src"/> as one block of a multi-block frame.
    /// <paramref name="frameRep"/> is the frame-scoped repeat-offset history
    /// (RFC 8878 §4.1.1: initialized to <c>{1, 4, 8}</c> at frame start and
    /// carried across blocks — the decoder resolves repeat codes against it,
    /// so resetting it per block would corrupt later blocks). It is updated
    /// in place for the next block, but ONLY when the block is accepted
    /// (return ≥ 0): on failure (-1) the history is restored, mirroring
    /// upstream <c>ZSTD_blockState_confirmRepcodesAndEntropyTables</c>, which
    /// runs only for emitted compressed blocks. A raw fallback must therefore
    /// never advance the history either — the caller restores its snapshot
    /// when it overrides an accepted block with raw. See
    /// <see cref="EncodeBlock(ReadOnlySpan{byte},int,byte[],int,int,bool)"/>
    /// for the return contract.
    /// </summary>
    public static int EncodeBlock(
        ReadOnlySpan<byte> src, int level,
        byte[] dst, int dstOffset, int dstCapacity, bool lastBlock, uint[] frameRep)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(frameRep);
        ArgumentOutOfRangeException.ThrowIfNegative(dstOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(dstCapacity);
        if (frameRep.Length < ZstdSeq.RepNum)
        {
            throw new ArgumentException("Repeat history needs 3 entries.", nameof(frameRep));
        }

        if (dstOffset + dstCapacity > dst.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(dstCapacity));
        }

        var r0 = frameRep[0];
        var r1 = frameRep[1];
        var r2 = frameRep[2];
        try
        {
            return EncodeBlockInner(src, level, dst, dstOffset, dstCapacity, lastBlock, frameRep);
        }
        catch (ZstdException)
        {
            frameRep[0] = r0;
            frameRep[1] = r1;
            frameRep[2] = r2;
            return -1;
        }
    }

    private static int EncodeBlockInner(
        ReadOnlySpan<byte> src, int level,
        byte[] dst, int dstOffset, int dstCapacity, bool lastBlock, uint[] rep)
    {
        if (dstCapacity < BlockHeaderSize + 2)
        {
            throw new ZstdException("Block destination too small.");
        }

        var prm = ZstdCompressionParameters.ForSizeAndLevel(src.Length, level).AdjustForSize(src.Length);
        var payload = EncodeBlockPayload(src, level, prm, dst, dstOffset + BlockHeaderSize, dstCapacity - BlockHeaderSize, rep);
        if (payload > MaxBlockPayload)
        {
            throw new ZstdException("Block too large.");
        }

        // Block header: 3 bytes LE — lastBlock flag, type compressed(2), size.
        var header = (lastBlock ? 1u : 0u) | ((uint)BlockTypeCompressed << 1) | ((uint)payload << 3);
        dst[dstOffset] = (byte)header;
        dst[dstOffset + 1] = (byte)(header >> 8);
        dst[dstOffset + 2] = (byte)(header >> 16);
        return BlockHeaderSize + payload;
    }

    /// <summary>
    /// Encodes <paramref name="src"/> as the payload (literals section +
    /// sequences section, no block header) of one frame block, using the
    /// explicitly supplied frame-level <paramref name="prm"/> row (see
    /// <see cref="ZstdMatchFinder.FindMatches(ReadOnlySpan{byte}, ZstdSequenceStore, uint[], ZstdCompressionParameters)"/> for why multi-block frames
    /// share one row). Returns the payload length. Throws
    /// <see cref="ZstdException"/> when the payload does not fit
    /// (<paramref name="dstCapacity"/> too small) or needs the unimplemented
    /// long-offsets path — callers decline to a raw block instead (mirrors
    /// C's <c>dstSize_tooSmall</c> → <c>ZSTD_noCompressBlock</c> fallback).
    /// Repeat history (<paramref name="rep"/>) is updated in place for the
    /// next block; callers restore their snapshot when they override the
    /// payload with raw/RLE (upstream confirms repcode history only for
    /// emitted compressed blocks).
    /// </summary>
    internal static int EncodeBlockPayload(
        ReadOnlySpan<byte> src, int level, ZstdCompressionParameters prm,
        byte[] dst, int dstOffset, int dstCapacity, uint[] rep)
    {
        var end = dstOffset + dstCapacity;
        if (dstCapacity < 2)
        {
            throw new ZstdException("Block destination too small.");
        }

        var finder = new ZstdMatchFinder(level);
        var store = new ZstdSequenceStore(Math.Max(1, src.Length));
        var srcCopy = src.ToArray();
        finder.FindMatches(srcCopy, store, rep, prm);
        // Standalone block: entropy starts with no previous tables (first-
        // block behavior); the staged next state is discarded.
        return EncodeStore(store, prm.Strategy, dst, dstOffset, end, new ZstdEntropyState(), new ZstdEntropyState());
    }

    /// <summary>
    /// Encodes one frame block <c>[blockStart, blockEnd)</c> with the frame
    /// state's persistent match tables (M2) and entropy tables (M3). The next
    /// entropy state stages into the frame; the frame writer confirms it only
    /// for emitted compressed blocks. Same return/throw contract as
    /// <see cref="EncodeBlockPayload(ReadOnlySpan{byte},int,ZstdCompressionParameters,byte[],int,int,uint[])"/>.
    /// </summary>
    internal static int EncodeBlockPayloadStateful(
        ZstdFrameState state, int blockStart, int blockEnd, byte[] dst, int dstOffset, int dstCapacity, uint[] rep)
    {
        ArgumentNullException.ThrowIfNull(state);
        var end = dstOffset + dstCapacity;
        if (dstCapacity < 2)
        {
            throw new ZstdException("Block destination too small.");
        }

        var store = new ZstdSequenceStore(Math.Max(1, blockEnd - blockStart));
        state.FindMatches(blockStart, blockEnd, store, rep);
        return EncodeStoreStateful(state, store, dst, dstOffset, end);
    }

    /// <summary>
    /// Encodes an already-parsed <paramref name="store"/> with the frame
    /// state's entropy tables, staging the next state (the splitter parses
    /// once, then encodes each partition from its derived store — re-parsing
    /// would double-insert match-table entries and corrupt later blocks).
    /// <paramref name="end"/> is the absolute output limit. Same staging
    /// contract as <see cref="EncodeBlockPayloadStateful"/>.
    /// </summary>
    internal static int EncodeStoreStateful(
        ZstdFrameState state, ZstdSequenceStore store, byte[] dst, int dstOffset, int end)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);
        // M3: the next entropy state stages into the frame; the caller
        // confirms it only when it emits the block compressed (raw/RLE
        // overrides decline it, like ZSTD_noCompressBlock).
        var next = new ZstdEntropyState();
        var size = EncodeStore(store, state.Prm.Strategy, dst, dstOffset, end, state.Entropy, next);
        state.StageEntropy(next);
        return size;
    }

    internal static int EncodeStore(
        ZstdSequenceStore store, ZstdStrategy strategy, byte[] dst, int dstOffset, int end,
        ZstdEntropyState prev, ZstdEntropyState next)
    {
        var nbSeq = store.Count;
        var pos = dstOffset;
        pos += EncodeLiteralsSection(store, nbSeq, strategy, dst, pos, end, prev, next);
        pos += EncodeSequencesSection(store, nbSeq, strategy, dst, pos, end, prev, next);
        return pos - dstOffset;
    }

    /// <summary>
    /// <c>ZSTD_minGain</c> (<c>lib/compress/zstd_compress_internal.h</c>):
    /// minimum compression gain (in bytes) required to emit a compressed
    /// block or literals section — the same formula gates both. A payload of
    /// <c>srcSize - MinGain</c> or more declines to raw.
    /// </summary>
    internal static int MinGain(int srcSize, ZstdStrategy strategy)
    {
        var minlog = strategy >= ZstdStrategy.BtUltra ? (int)strategy - 1 : 6;
        return (srcSize >> minlog) + 2;
    }

    // ------------------------------------------------------------------
    // Literals section (ZSTD_compressLiterals)
    // ------------------------------------------------------------------

    private static int EncodeLiteralsSection(
        ZstdSequenceStore store, int nbSeq, ZstdStrategy strategy, byte[] dst, int pos, int end,
        ZstdEntropyState prev, ZstdEntropyState next)
    {
        // Every exit stages the next Huffman state (native memcpy(nextHuf,
        // prevHuf) at the top of ZSTD_compressLiterals); paths below overwrite
        // it when they build or validate a table.
        next.HufRepeat = prev.HufRepeat;
        next.HufTable = prev.HufTable;

        var start = pos;
        var litLen = store.LiteralLength + store.TrailingLength;
        if (litLen == 0)
        {
            Ensure(dst, pos, end, 1);
            dst[pos++] = (byte)SetBasic; // Raw, sizeFormat 0, regen 0.
            return pos - start;
        }

        var litBuf = new byte[litLen];
        store.Literals.CopyTo(new Span<byte>(litBuf, 0, store.LiteralLength));
        store.TrailingLiterals.CopyTo(new Span<byte>(litBuf, store.LiteralLength, store.TrailingLength));

        // Too small: don't even attempt compression (ZSTD_minLiteralsToCompress:
        // 8 << min(9-strategy, 3), or 6 with a valid repeat table).
        var shift = Math.Min(9 - (int)strategy, 3);
        var minLit = prev.HufRepeat == ZstdHufRepeat.Valid ? 6 : 8 << shift;
        if (litLen < minLit)
        {
            return WriteRawOrRle(dst, pos, end, litLen, SetBasic, 0, litBuf);
        }

        // Suspect-uncompressible sampling gate (SUSPECT_UNCOMPRESSIBLE_LITERAL_RATIO).
        var suspect = nbSeq == 0 || litLen / Math.Max(1, nbSeq) >= 20;

        // Huffman attempt with previous-table reuse (HUF_compress1X/4X_repeat):
        // output (table description + streams, or bare treeless streams) lands
        // after the header slot. Strategies below lazy prefer the old table
        // for inputs up to 1 KiB; btultra and above probe the optimal depth.
        var lhSize = 3 + (litLen >= 1024 ? 1 : 0) + (litLen >= 16384 ? 1 : 0);
        var preferRepeat = strategy < ZstdStrategy.Lazy && litLen <= 1024;
        var huffSize = 0;
        HuffmanCTable? huffTable = null;
        var repeat = prev.HufRepeat;
        if (end > (pos + lhSize))
        {
            huffSize = ZstdHuffmanEncoder.CompressWithRepeat(
                dst, pos + lhSize, end - (pos + lhSize), litBuf, 0, litLen,
                prev.HufTable, ref repeat, preferRepeat,
                suspectUncompressible: suspect,
                optimalDepth: strategy >= ZstdStrategy.BtUltra,
                out huffTable);
        }

        // Minimum gain gate (ZSTD_minGain, same formula for blocks and literals).
        var minGain = MinGain(litLen, strategy);
        if (huffSize == 0 || huffSize >= litLen - minGain)
        {
            return WriteRawOrRle(dst, pos, end, litLen, SetBasic, 0, litBuf);
        }

        if (huffSize == 1)
        {
            // Single-symbol alphabet: RLE when large or truly uniform.
            if (litLen >= 8 || AllIdentical(litBuf, out _))
            {
                return WriteRawOrRle(dst, pos, end, litLen, SetRle, litBuf[0]);
            }

            return WriteRawOrRle(dst, pos, end, litLen, SetBasic, 0, litBuf);
        }

        // Treeless (reused) streams carry no table description; the staged
        // mode and table stay exactly as CompressWithRepeat left them (valid
        // stays valid, check stays check).
        uint hType = SetCompressed;
        if (huffTable is null)
        {
            hType = SetRepeat;
        }
        else
        {
            next.HufTable = huffTable;
            next.HufRepeat = ZstdHufRepeat.Check;
        }

        // The stream layout matches the encoder's choice (single below 256
        // literals, or below 1 KiB with a valid table).
        var singleStream = litLen < ZstdHuffmanEncoder.SingleStreamThreshold
            || (prev.HufRepeat == ZstdHufRepeat.Valid && litLen < 1024);
        var sizeFormat = lhSize switch
        {
            3 => (singleStream ? 0 : 1),
            4 => 2,
            _ => 3
        };
        var regenBits = sizeFormat <= 1 ? 10 : sizeFormat == 2 ? 14 : 18;
        if (litLen >= (1 << regenBits) || huffSize >= (1 << regenBits))
        {
            throw new ZstdException("Literals size exceeds header field.");
        }

        Ensure(dst, pos, end, lhSize + huffSize);
        WriteLiteralsCompressedHeader(dst, pos, hType, lhSize, sizeFormat, litLen, huffSize);
        return lhSize + huffSize; // Huffman bytes already in place.
    }

    private static void WriteLiteralsCompressedHeader(
        byte[] dst, int pos, uint hType, int lhSize, int sizeFormat, int regen, int compSize)
    {
        var header = hType + ((uint)sizeFormat << 2)
                           + ((uint)regen << 4);
        if (lhSize == 3)
        {
            header += (uint)compSize << 14;
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
        }
        else if (lhSize == 4)
        {
            header += (uint)compSize << 18;
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
            dst[pos + 3] = (byte)(header >> 24);
        }
        else
        {
            header += (uint)(compSize & 0x3FF) << 22;
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
            dst[pos + 3] = (byte)(header >> 24);
            dst[pos + 4] = (byte)(compSize >> 10);
        }
    }

    private static int WriteRawOrRle(
        byte[] dst, int pos, int end, int litLen, uint mode, byte value, byte[]? litBuf = null)
    {
        var start = pos;
        var flSize = 1 + (litLen > 31 ? 1 : 0) + (litLen > 4095 ? 1 : 0);
        var extra = mode == SetRle ? 1 : litLen;
        Ensure(dst, pos, end, flSize + extra);
        if (flSize == 1)
        {
            dst[pos] = (byte)(mode + ((uint)litLen << 3));
        }
        else if (flSize == 2)
        {
            var header = mode + (1u << 2) + ((uint)litLen << 4);
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
        }
        else
        {
            var header = mode + (3u << 2) + ((uint)litLen << 4);
            dst[pos] = (byte)header;
            dst[pos + 1] = (byte)(header >> 8);
            dst[pos + 2] = (byte)(header >> 16);
            dst[pos + 3] = (byte)(header >> 24);
        }

        pos += flSize;
        if (mode == SetRle)
        {
            dst[pos++] = value;
        }
        else
        {
            litBuf!.CopyTo(new Span<byte>(dst, pos, litLen));
            pos += litLen;
        }

        return pos - start;
    }

    private static bool AllIdentical(byte[] buf, out byte value)
    {
        value = buf[0];
        for (var i = 1; i < buf.Length; i++)
        {
            if (buf[i] != value)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Sequences section (ZSTD_seqToCodes + ZSTD_buildCTable + encode loop)
    // ------------------------------------------------------------------

    private static int EncodeSequencesSection(
        ZstdSequenceStore store, int nbSeq, ZstdStrategy strategy, byte[] dst, int pos, int end,
        ZstdEntropyState prev, ZstdEntropyState next)
    {
        var start = pos;
        if (nbSeq == 0)
        {
            // Literals-only block: FSE tables carry over as if repeated.
            next.LlRepeat = prev.LlRepeat;
            next.OfRepeat = prev.OfRepeat;
            next.MlRepeat = prev.MlRepeat;
            next.LlTable = prev.LlTable;
            next.OfTable = prev.OfTable;
            next.MlTable = prev.MlTable;
            Ensure(dst, pos, end, 1);
            dst[pos++] = 0;
            return pos - start;
        }

        // --- Convert sequences to codes (ZSTD_seqToCodes) ---
        var codes = DeriveCodes(store, nbSeq);
        byte[] llCodes = codes.Ll, ofCodes = codes.Of, mlCodes = codes.Ml;
        uint[] litLens = codes.LitLens, mlBases = codes.MlBases, offBases = codes.OffBases;

        // --- Build per-alphabet tables (ZSTD_selectEncodingType: repeat the
        // previous table when its estimated cost wins, else rebuild) ---
        var llMode = prev.LlRepeat;
        var ofMode = prev.OfRepeat;
        var mlMode = prev.MlRepeat;
        var ll = BuildSeqTable(codes.LlCount, llCodes, nbSeq, codes.LlMax, LlFseLog,
            LlDefaultNorm, LlDefaultNormLog, MaxLl, defaultAllowed: true, strategy,
            prev.LlTable, ref llMode);
        var of = BuildSeqTable(codes.OfCount, ofCodes, nbSeq, codes.OfMax, OffFseLog,
            OfDefaultNorm, OfDefaultNormLog, DefaultMaxOff, defaultAllowed: codes.OfMax <= DefaultMaxOff, strategy,
            prev.OfTable, ref ofMode);
        var ml = BuildSeqTable(codes.MlCount, mlCodes, nbSeq, codes.MlMax, MlFseLog,
            MlDefaultNorm, MlDefaultNormLog, MaxMl, defaultAllowed: true, strategy,
            prev.MlTable, ref mlMode);
        next.LlRepeat = llMode;
        next.OfRepeat = ofMode;
        next.MlRepeat = mlMode;
        next.LlTable = ll.Table;
        next.OfTable = of.Table;
        next.MlTable = ml.Table;

        // --- Section header: nbSeq, modes, table descriptions (LL, OF, ML) ---
        pos += WriteNbSeq(dst, pos, end, nbSeq);
        Ensure(dst, pos, end, 1);
        dst[pos++] = (byte)((ll.Mode << 6) | (of.Mode << 4) | (ml.Mode << 2));
        var lastCountSize = 0;
        pos += WriteSeqTableDesc(dst, pos, end, ll, llCodes[0], ref lastCountSize);
        pos += WriteSeqTableDesc(dst, pos, end, of, ofCodes[0], ref lastCountSize);
        pos += WriteSeqTableDesc(dst, pos, end, ml, mlCodes[0], ref lastCountSize);

        var llTable = ll.Table;
        var ofTable = of.Table;
        var mlTable = ml.Table;
        int llLog = ll.TableLog, ofLog = of.TableLog, mlLog = ml.TableLog;

        // --- Bitstream (ZSTD_encodeSequences_body, 64-bit schedule) ---
        // Any ZstdException below (bitstream capacity) propagates to
        // EncodeBlock, which declines to raw (C's dstSize_tooSmall path).
        var bs = new CStreamWriter(dst, pos, end - pos);
        var last = nbSeq - 1;
        var stateMl = ZstdFseEncoder.InitCState2(mlTable, mlCodes[last]);
        var stateOf = ZstdFseEncoder.InitCState2(ofTable, ofCodes[last]);
        var stateLl = ZstdFseEncoder.InitCState2(llTable, llCodes[last]);
        AddBitsChecked(bs, litLens[last], LlBits[llCodes[last]]);
        AddBitsChecked(bs, mlBases[last], MlBits[mlCodes[last]]);
        AddBitsChecked(bs, offBases[last], ofCodes[last]);
        bs.FlushBits();

        for (var n = nbSeq - 2; n >= 0; n--)
        {
            byte llCode = llCodes[n], ofCode = ofCodes[n], mlCode = mlCodes[n];
            int llExtra = LlBits[llCode], mlExtra = MlBits[mlCode];
            stateOf = EncodeSeqSymbol(bs, ofTable, stateOf, ofCode, ofLog);
            stateMl = EncodeSeqSymbol(bs, mlTable, stateMl, mlCode, mlLog);
            stateLl = EncodeSeqSymbol(bs, llTable, stateLl, llCode, llLog);
            if (ofCode + mlExtra + llExtra >= 64 - 7 - (llLog + mlLog + ofLog))
            {
                bs.FlushBits();
            }

            AddBitsChecked(bs, litLens[n], llExtra);
            AddBitsChecked(bs, mlBases[n], mlExtra);
            if (ofCode + mlExtra + llExtra > 56)
            {
                bs.FlushBits();
            }

            AddBitsChecked(bs, offBases[n], ofCode);
            bs.FlushBits();
        }

        FlushStateChecked(bs, stateMl, mlLog);
        FlushStateChecked(bs, stateOf, ofLog);
        FlushStateChecked(bs, stateLl, llLog);

        var streamSize = bs.Close();
        // zstd ≤ 1.3.4 mis-decodes a trailing compressed table below 4 bytes
        // total with the bitstream: emit raw instead (never worth optimizing).
        if (lastCountSize > 0 && lastCountSize + streamSize < 4)
        {
            throw new ZstdException("Block too small for decoders before 1.3.5.");
        }

        return (pos - start) + streamSize;
    }

    /// <summary>
    /// One sequence alphabet: the compression table plus, for FSE mode, the
    /// normalized counts the NCount header is written from.
    /// </summary>
    /// <param name="Table">Compression table.</param>
    /// <param name="Mode">Table mode (RLE or FSE).</param>
    /// <param name="TableLog">Accuracy log for FSE tables.</param>
    /// <param name="Norm">Normalized counts for FSE mode, null for RLE.</param>
    /// <param name="MaxObserved">Maximum observed symbol value.</param>
    internal readonly record struct SeqAlphabet(
        FseCTable Table,
        int Mode,
        int TableLog,
        short[]? Norm,
        int MaxObserved);

    /// <summary>
    /// Per-sequence codes and values for one sequence run
    /// (<c>ZSTD_seqToCodes</c> output: LL/OF/ML code bytes plus the literal
    /// lengths, match-length bases (<c>matchLength - MINMATCH</c>) and offset
    /// bases the bitstream writes, with the histogram counts and maxima the
    /// table builders need). Lengths are resolved (long-length +0x10000
    /// applied), which yields the same codes as native's truncated-plus-patch
    /// rule.
    /// </summary>
    /// <param name="Ll">Literal-length codes.</param>
    /// <param name="Of">Offset codes.</param>
    /// <param name="Ml">Match-length codes.</param>
    /// <param name="LitLens">Literal run lengths.</param>
    /// <param name="MlBases">Match-length bases.</param>
    /// <param name="OffBases">Offset bases.</param>
    /// <param name="LlCount">Literal-length code histogram.</param>
    /// <param name="OfCount">Offset code histogram.</param>
    /// <param name="MlCount">Match-length code histogram.</param>
    /// <param name="LlMax">Maximum literal-length code.</param>
    /// <param name="OfMax">Maximum offset code.</param>
    /// <param name="MlMax">Maximum match-length code.</param>
    internal readonly record struct SeqCodes(
        byte[] Ll, byte[] Of, byte[] Ml,
        uint[] LitLens, uint[] MlBases, uint[] OffBases,
        uint[] LlCount, uint[] OfCount, uint[] MlCount,
        int LlMax, int OfMax, int MlMax);

    /// <summary>
    /// Converts <paramref name="nbSeq"/> sequences from
    /// <paramref name="store"/> to codes (<c>ZSTD_seqToCodes</c>). Throws
    /// <see cref="ZstdException"/> on the unimplemented long-offsets path
    /// (offset codes ≥ 32).
    /// </summary>
    internal static SeqCodes DeriveCodes(ZstdSequenceStore store, int nbSeq)
    {
        ArgumentNullException.ThrowIfNull(store);
        var llCodes = new byte[nbSeq];
        var ofCodes = new byte[nbSeq];
        var mlCodes = new byte[nbSeq];
        var litLens = new uint[nbSeq];
        var mlBases = new uint[nbSeq];
        var offBases = new uint[nbSeq];
        var llCount = new uint[MaxLl + 1];
        var ofCount = new uint[MaxOff + 1];
        var mlCount = new uint[MaxMl + 1];
        int llMax = 0, ofMax = 0, mlMax = 0;
        for (var i = 0; i < nbSeq; i++)
        {
            var seq = store.Get(i);
            var llCode = LLcode(seq.LitLength);
            var mlBase = seq.MatchLength - (uint)ZstdSeq.MinMatch;
            var mlCode = MLcode(mlBase);
            var ofCode = BitOperations.Log2(seq.OffBase);
            if (ofCode >= 32)
            {
                // longOffsets path: unreachable with windowLog <= 17.
                throw new ZstdException("Offset code too large.");
            }

            llCodes[i] = (byte)llCode;
            ofCodes[i] = (byte)ofCode;
            mlCodes[i] = (byte)mlCode;
            litLens[i] = seq.LitLength;
            mlBases[i] = mlBase;
            offBases[i] = seq.OffBase;
            llCount[llCode]++;
            ofCount[ofCode]++;
            mlCount[mlCode]++;
            llMax = Math.Max(llMax, llCode);
            ofMax = Math.Max(ofMax, ofCode);
            mlMax = Math.Max(mlMax, mlCode);
        }

        return new SeqCodes(
            llCodes, ofCodes, mlCodes, litLens, mlBases, offBases,
            llCount, ofCount, mlCount, llMax, ofMax, mlMax);
    }

    /// <summary>
    /// Builds one sequence-alphabet table, exactly like
    /// <c>ZSTD_selectEncodingType</c> + <c>ZSTD_buildCTable</c>: single-symbol
    /// with <c>nbSeq ≤ 2</c> (and default allowed) → basic, single-symbol
    /// otherwise → RLE, else the <c>strategy &lt; lazy</c> count heuristic
    /// (fast/double-fast/greedy, repeating a valid table below 1000
    /// sequences) or the basic-vs-repeat-vs-compressed cost comparison (lazy
    /// and above). A repeat selection keeps the incoming
    /// <paramref name="mode"/> (valid stays valid, check stays check) and
    /// shares the previous table; anything else overwrites the mode (basic
    /// and RLE clear it, compressed arms it for validation). Compressed uses
    /// the last-symbol count decrement of <c>ZSTD_buildCTable</c>.
    /// </summary>
    internal static SeqAlphabet BuildSeqTable(
        uint[] count, byte[] codes, int nbSeq, int maxObserved, int maxLog,
        short[] defaultNorm, int defaultNormLog, int defaultMax, bool defaultAllowed,
        ZstdStrategy strategy, FseCTable? prevTable, ref ZstdFseRepeat mode)
    {
        uint mostFrequent = 0;
        for (var s = 0; s <= maxObserved; s++)
        {
            mostFrequent = Math.Max(mostFrequent, count[s]);
        }

        if (mostFrequent == (uint)nbSeq)
        {
            mode = ZstdFseRepeat.None;
            if (defaultAllowed && nbSeq <= 2)
            {
                var basicTable = ZstdFseEncoder.BuildCTable(defaultNorm, defaultMax, defaultNormLog);
                return new SeqAlphabet(basicTable, SeqModeBasic, defaultNormLog, null, defaultMax);
            }

            return new SeqAlphabet(ZstdFseEncoder.RleTable(codes[0]), SeqModeRle, 0, null, maxObserved);
        }

        if (strategy < ZstdStrategy.Lazy)
        {
            // Fast heuristic (strategies below lazy skip the cost model).
            if (defaultAllowed)
            {
                var mult = 10 - (int)strategy; // fast 9, double-fast 8, greedy 7
                var dynamicMin = ((1 << defaultNormLog) * mult) >> 3;
                if (mode == ZstdFseRepeat.Valid && prevTable is not null && nbSeq < 1000)
                {
                    return new SeqAlphabet(prevTable, SeqModeRepeat, prevTable.TableLog, null, prevTable.MaxSymbolValue);
                }

                if (nbSeq < dynamicMin || mostFrequent < (uint)(nbSeq >> (defaultNormLog - 1)))
                {
                    mode = ZstdFseRepeat.None;
                    var basicTable = ZstdFseEncoder.BuildCTable(defaultNorm, defaultMax, defaultNormLog);
                    return new SeqAlphabet(basicTable, SeqModeBasic, defaultNormLog, null, defaultMax);
                }
            }
        }
        else
        {
            // Full cost model (lazy and above): basic and repeat estimates
            // against a fresh table (the previous table scores ulong.MaxValue
            // when it cannot cover the distribution, like ERROR(GENERIC)).
            var basicCost = defaultAllowed
                ? CrossEntropyCost(defaultNorm, (uint)defaultNormLog, count, (uint)maxObserved)
                : ulong.MaxValue;
            var repeatCost = mode != ZstdFseRepeat.None && prevTable is not null
                ? ZstdFseEncoder.FseBitCost(prevTable, count, maxObserved)
                : ulong.MaxValue;
            var ncountCost = NCountCost(count, maxObserved, nbSeq, maxLog);
            var compressedCost = (ncountCost << 3) + EntropyCost(count, (uint)maxObserved, nbSeq);
            if (defaultAllowed && basicCost <= repeatCost && basicCost <= compressedCost)
            {
                mode = ZstdFseRepeat.None;
                var basicTable = ZstdFseEncoder.BuildCTable(defaultNorm, defaultMax, defaultNormLog);
                return new SeqAlphabet(basicTable, SeqModeBasic, defaultNormLog, null, defaultMax);
            }

            if (repeatCost <= compressedCost)
            {
                return new SeqAlphabet(prevTable!, SeqModeRepeat, prevTable!.TableLog, null, prevTable!.MaxSymbolValue);
            }
        }

        mode = ZstdFseRepeat.Check;

        var tableLog = ZstdFseEncoder.OptimalTableLog(maxLog, nbSeq, maxObserved);
        var total = nbSeq;
        var lastCode = codes[nbSeq - 1];
        if (count[lastCode] > 1)
        {
            count[lastCode]--;
            total--;
        }

        var norm = new short[maxObserved + 1];
        var useLowProb = total >= 2048; // ZSTD_useLowProbCount.
        var got = ZstdFseEncoder.NormalizeCounts(norm, count, total, maxObserved, tableLog, useLowProb);
        if (got == -1)
        {
            throw new ZstdException("Sequence alphabet unexpectedly RLE.");
        }

        var table = ZstdFseEncoder.BuildCTable(norm, maxObserved, tableLog);
        return new SeqAlphabet(table, SeqModeFse, tableLog, norm, maxObserved);
    }

    // -log2(x/256) LUT from lib/compress/zstd_compress_sequences.c.
    private static readonly uint[] InverseProbLog256 =
    [
        0, 2048, 1792, 1642, 1536, 1453, 1386, 1329, 1280, 1236, 1197, 1162,
        1130, 1100, 1073, 1047, 1024, 1001, 980, 960, 941, 923, 906, 889,
        874, 859, 844, 830, 817, 804, 791, 779, 768, 756, 745, 734,
        724, 714, 704, 694, 685, 676, 667, 658, 650, 642, 633, 626,
        618, 610, 603, 595, 588, 581, 574, 567, 561, 554, 548, 542,
        535, 529, 523, 517, 512, 506, 500, 495, 489, 484, 478, 473,
        468, 463, 458, 453, 448, 443, 438, 434, 429, 424, 420, 415,
        411, 407, 402, 398, 394, 390, 386, 382, 377, 373, 370, 366,
        362, 358, 354, 350, 347, 343, 339, 336, 332, 329, 325, 322,
        318, 315, 311, 308, 305, 302, 298, 295, 292, 289, 286, 282,
        279, 276, 273, 270, 267, 264, 261, 258, 256, 253, 250, 247,
        244, 241, 239, 236, 233, 230, 228, 225, 222, 220, 217, 215,
        212, 209, 207, 204, 202, 199, 197, 194, 192, 190, 187, 185,
        182, 180, 178, 175, 173, 171, 168, 166, 164, 162, 159, 157,
        155, 153, 151, 149, 146, 144, 142, 140, 138, 136, 134, 132,
        130, 128, 126, 123, 121, 119, 117, 115, 114, 112, 110, 108,
        106, 104, 102, 100, 98, 96, 94, 93, 91, 89, 87, 85,
        83, 82, 80, 78, 76, 74, 73, 71, 69, 67, 66, 64,
        62, 61, 59, 57, 55, 54, 52, 50, 49, 47, 46, 44,
        42, 41, 39, 37, 36, 34, 33, 31, 30, 28, 26, 25,
        23, 22, 20, 19, 17, 16, 14, 13, 11, 10, 8, 7,
        5, 4, 2, 1,
    ];

    internal static ulong CrossEntropyCost(        short[] norm, uint accuracyLog, uint[] count, uint max)
    {
        var shift = 8 - (int)accuracyLog;
        ulong cost = 0;
        for (uint s = 0; s <= max; s++)
        {
            var normAcc = norm[s] != -1 ? (uint)norm[s] : 1;
            var norm256 = normAcc << shift;
            cost += (ulong)count[s] * InverseProbLog256[norm256];
        }

        return cost >> 8;
    }

    internal static ulong EntropyCost(uint[] count, uint max, int total)
    {
        ulong cost = 0;
        for (uint s = 0; s <= max; s++)
        {
            var norm = (uint)(((ulong)256 * count[s]) / (uint)total);
            if (count[s] != 0 && norm == 0)
            {
                norm = 1;
            }

            cost += (ulong)count[s] * InverseProbLog256[norm];
        }

        return cost >> 8;
    }

    internal static ulong NCountCost(uint[] count, int max, int nbSeq, int fseLog)
    {
        var tableLog = ZstdFseEncoder.OptimalTableLog(fseLog, nbSeq, max);
        var norm = new short[max + 1];
        var work = (uint[])count.Clone();
        var useLowProb = nbSeq >= 2048;
        var got = ZstdFseEncoder.NormalizeCounts(norm, work, nbSeq, max, tableLog, useLowProb);
        if (got == -1)
        {
            return 0;
        }

        var buf = new byte[ZstdFseEncoder.NCountBound];
        var size = ZstdFseEncoder.WriteNCount(buf, 0, buf.Length, norm, max, tableLog);
        return (ulong)size;
    }

    private static int WriteSeqTableDesc(
        byte[] dst, int pos, int end, SeqAlphabet alphabet, byte rleSymbol, ref int lastCountSize)
    {
        if (alphabet.Mode == SeqModeBasic || alphabet.Mode == SeqModeRepeat)
        {
            return 0;
        }

        if (alphabet.Mode == SeqModeRle)
        {
            Ensure(dst, pos, end, 1);
            dst[pos++] = rleSymbol;
            return 1;
        }

        var size = ZstdFseEncoder.WriteNCount(
            dst, pos, end - pos, alphabet.Norm!, alphabet.MaxObserved, alphabet.TableLog);
        lastCountSize = size;
        return size;
    }

    private static int WriteNbSeq(byte[] dst, int pos, int end, int nbSeq)
    {
        if (nbSeq < 128)
        {
            Ensure(dst, pos, end, 1);
            dst[pos++] = (byte)nbSeq;
            return 1;
        }

        if (nbSeq < LongNbSeq)
        {
            Ensure(dst, pos, end, 2);
            dst[pos++] = (byte)(128 + (nbSeq >> 8));
            dst[pos++] = (byte)(nbSeq & 0xFF);
            return 2;
        }

        Ensure(dst, pos, end, 3);
        dst[pos++] = 255;
        dst[pos++] = (byte)((nbSeq - LongNbSeq) & 0xFF);
        dst[pos++] = (byte)(((nbSeq - LongNbSeq) >> 8) & 0xFF);
        return 3;
    }

    private static long EncodeSeqSymbol(
        CStreamWriter bs, FseCTable table, long state, byte symbol, int tableLog)
    {
        if (table.TableLog != 0 && bs.BitPos + tableLog >= 64)
        {
            bs.FlushBits();
        }

        return ZstdFseEncoder.EncodeCStateSymbol(bs, table, state, symbol);
    }

    private static void AddBitsChecked(CStreamWriter bs, ulong value, int nbBits)
    {
        if (nbBits > 0 && bs.BitPos + nbBits >= 64)
        {
            bs.FlushBits();
        }

        bs.AddBits(value, nbBits);
    }

    private static void FlushStateChecked(CStreamWriter bs, long state, int tableLog)
    {
        if (tableLog > 0 && bs.BitPos + tableLog >= 64)
        {
            bs.FlushBits();
        }

        ZstdFseEncoder.FlushCState(bs, state, tableLog);
    }

    internal static int LLcode(uint litLength)
    {
        return litLength > 63 ? BitOperations.Log2(litLength) + 19 : LlCodeTable[litLength];
    }

    internal static int MLcode(uint mlBase)
    {
        return mlBase > 127 ? BitOperations.Log2(mlBase) + 36 : MlCodeTable[mlBase];
    }

    /// <summary>Extra-bits per literal-length code (shared with the optimal parser).</summary>
    internal static byte LlExtraBits(int code)
    {
        return LlBits[code];
    }

    /// <summary>Extra-bits per match-length code (shared with the optimal parser).</summary>
    internal static byte MlExtraBits(int code)
    {
        return MlBits[code];
    }

    private static void Ensure(byte[] dst, int pos, int end, int need)
    {
        _ = dst;
        if (need < 0 || pos + need > end)
        {
            throw new ZstdException("Block destination too small.");
        }
    }
}
