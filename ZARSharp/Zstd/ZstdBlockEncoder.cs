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
/// Deliberate simplifications (validity-preserving, documented in the port
/// plan §9): tables are written fresh per block, so <c>set_repeat</c>
/// (sequence tables) and treeless literals never occur;
/// <c>set_basic</c> (predefined sequence tables) is never selected, every
/// multi-symbol alphabet uses a fresh FSE table and every single-symbol
/// alphabet uses RLE; <c>longOffsets</c> (offset codes ≥ 32, impossible with
/// windowLog ≤ 17) declines to encode instead of emitting a wrong stream.
/// </summary>
internal static class ZstdBlockEncoder
{
    private const int BlockHeaderSize = 3;
    private const int BlockTypeCompressed = 2;
    private const int MaxBlockPayload = (1 << 21) - 1;

    // Sequence-alphabet accuracy logs (LLFSELog / MLFSELog / OffFSELog):
    // also the decoder's acceptance caps (ZstdDecompressor rereads them).
    private const int LlFseLog = 9;
    private const int MlFseLog = 9;
    private const int OffFseLog = 8;

    private const int MaxLl = 35;
    private const int MaxMl = 52;
    private const int MaxOff = 31;

    // Literals-section mode tags (shared with the raw/RLE header forms).
    private const uint SetBasic = 0; // Raw literals.
    private const uint SetRle = 1; // RLE literals.
    private const uint SetCompressed = 2; // Huffman-compressed literals.

    // Sequence-section mode tags (2 bits per alphabet in the modes byte).
    private const int SeqModeRle = 1;
    private const int SeqModeFse = 2;

    // nbSeq long form threshold (LONGNBSEQ).
    private const int LongNbSeq = 0x7F00;

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
    private static readonly byte[] LlBits =
    [
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
    /// <paramref name="level"/> (1..6). Repeat history starts fresh
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
        var end = dstOffset + dstCapacity;
        if (dstCapacity < BlockHeaderSize + 2)
        {
            throw new ZstdException("Block destination too small.");
        }

        var finder = new ZstdMatchFinder(level);
        var store = new ZstdSequenceStore(Math.Max(1, src.Length));
        var srcCopy = src.ToArray();
        finder.FindMatches(srcCopy, store, rep);
        var nbSeq = store.Count;

        var pos = dstOffset + BlockHeaderSize;
        pos += EncodeLiteralsSection(store, dst, pos, end);
        pos += EncodeSequencesSection(store, nbSeq, dst, pos, end);

        var payload = pos - (dstOffset + BlockHeaderSize);
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

    // ------------------------------------------------------------------
    // Literals section (ZSTD_compressLiterals)
    // ------------------------------------------------------------------

    private static int EncodeLiteralsSection(ZstdSequenceStore store, byte[] dst, int pos, int end)
    {
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

        // All bytes identical → RLE section (ZSTD_compressRleLiteralsBlock).
        if (AllIdentical(litBuf, out var repeated))
        {
            return WriteRawOrRle(dst, pos, end, litLen, SetRle, repeated);
        }

        // Huffman attempt, exactly like HUF_compress_internal without reuse:
        // output (table description + streams) lands after the header slot.
        var lhSize = 3 + (litLen >= 1024 ? 1 : 0) + (litLen >= 16384 ? 1 : 0);
        var huffSize = 0;
        if (end > (pos + lhSize))
        {
            huffSize = ZstdHuffmanEncoder.Compress(
                dst, pos + lhSize, end - (pos + lhSize), litBuf, 0, litLen);
        }

        if (huffSize >= 2)
        {
            var singleStream = litLen < ZstdHuffmanEncoder.SingleStreamThreshold;
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
            WriteLiteralsCompressedHeader(dst, pos, lhSize, sizeFormat, litLen, huffSize);
            return lhSize + huffSize; // Huffman bytes already in place.
        }

        // Raw fallback (ZSTD_noCompressLiterals), including huffSize 0/1 here
        // (1 would mean RLE, but all-identical was checked above, so treat as raw).
        return WriteRawOrRle(dst, pos, end, litLen, SetBasic, 0, litBuf);
    }

    private static void WriteLiteralsCompressedHeader(
        byte[] dst, int pos, int lhSize, int sizeFormat, int regen, int compSize)
    {
        var header = SetCompressed + ((uint)sizeFormat << 2)
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
        ZstdSequenceStore store, int nbSeq, byte[] dst, int pos, int end)
    {
        var start = pos;
        if (nbSeq == 0)
        {
            Ensure(dst, pos, end, 1);
            dst[pos++] = 0;
            return pos - start;
        }

        // --- Convert sequences to codes (ZSTD_seqToCodes) ---
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

        // --- Build per-alphabet tables (RLE or fresh FSE; never repeat/basic) ---
        var ll = BuildSeqTable(llCount, llCodes, nbSeq, llMax, LlFseLog);
        var of = BuildSeqTable(ofCount, ofCodes, nbSeq, ofMax, OffFseLog);
        var ml = BuildSeqTable(mlCount, mlCodes, nbSeq, mlMax, MlFseLog);

        // --- Section header: nbSeq, modes, table descriptions (LL, OF, ML) ---
        pos += WriteNbSeq(dst, pos, end, nbSeq);
        Ensure(dst, pos, end, 1);
        dst[pos++] = (byte)((ll.Mode << 6) | (of.Mode << 4) | (ml.Mode << 2));
        pos += WriteSeqTableDesc(dst, pos, end, ll, llCodes[0]);
        pos += WriteSeqTableDesc(dst, pos, end, of, ofCodes[0]);
        pos += WriteSeqTableDesc(dst, pos, end, ml, mlCodes[0]);

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
    private readonly record struct SeqAlphabet(
        FseCTable Table,
        int Mode,
        int TableLog,
        short[]? Norm,
        int MaxObserved);

    /// <summary>
    /// Builds one sequence-alphabet table: RLE when a single symbol holds all
    /// <paramref name="nbSeq"/> occurrences (mirrors the
    /// <c>mostFrequent == nbSeq</c> arm of <c>ZSTD_selectEncodingType</c>),
    /// otherwise a fresh FSE table (the <c>set_compressed</c> arm, including
    /// the last-symbol count decrement of <c>ZSTD_buildCTable</c>).
    /// </summary>
    private static SeqAlphabet BuildSeqTable(
        uint[] count, byte[] codes, int nbSeq, int maxObserved, int maxLog)
    {
        // Single-symbol test (mostFrequent == nbSeq, ZSTD_selectEncodingType).
        uint best = 0;
        for (var s = 0; s <= maxObserved; s++)
        {
            best = Math.Max(best, count[s]);
        }

        if (best == (uint)nbSeq)
        {
            return new SeqAlphabet(ZstdFseEncoder.RleTable(codes[0]), SeqModeRle, 0, null, maxObserved);
        }

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

    private static int WriteSeqTableDesc(
        byte[] dst, int pos, int end, SeqAlphabet alphabet, byte rleSymbol)
    {
        if (alphabet.Mode == SeqModeRle)
        {
            Ensure(dst, pos, end, 1);
            dst[pos++] = rleSymbol;
            return 1;
        }

        var size = ZstdFseEncoder.WriteNCount(
            dst, pos, end - pos, alphabet.Norm!, alphabet.MaxObserved, alphabet.TableLog);
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

    private static int LLcode(uint litLength)
    {
        return litLength > 63 ? BitOperations.Log2(litLength) + 19 : LlCodeTable[litLength];
    }

    private static int MLcode(uint mlBase)
    {
        return mlBase > 127 ? BitOperations.Log2(mlBase) + 36 : MlCodeTable[mlBase];
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