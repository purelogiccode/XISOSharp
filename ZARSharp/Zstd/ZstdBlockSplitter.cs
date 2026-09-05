using System.Buffers.Binary;

namespace ZARSharp.Zstd;

/// <summary>
/// Block splitter (M4): pre-split of 128 KiB input chunks by raw-byte
/// fingerprint (<c>ZSTD_optimalBlockSize</c> / <c>ZSTD_splitBlock</c>,
/// <c>lib/compress/zstd_preSplit.c</c>) and post-split of parsed blocks by
/// recursive entropy estimation
/// (<c>ZSTD_compressBlock_splitBlock</c> / <c>ZSTD_deriveBlockSplits</c>,
/// <c>lib/compress/zstd_compress.c</c>).
/// <para/>
/// Splitting only engages for optimal-parser strategies with a large enough
/// window (<c>ZSTD_resolveBlockSplitterMode</c>: strategy ≥ btopt and
/// windowLog ≥ 17); every other level keeps the single-block path exactly.
/// </summary>
internal static class ZstdBlockSplitter
{
    private const int BlockSizeMax = 131072; // ZSTD_BLOCKSIZE_MAX.
    private const int ChunkSize = 8 * 1024; // CHUNKSIZE.
    private const int SegmentSize = 512; // fromBorders SEGMENT_SIZE.
    private const int MinSequencesSplit = 300; // MIN_SEQUENCES_BLOCK_SPLITTING.
    private const int MaxBlockSplits = 196; // ZSTD_MAX_NB_BLOCK_SPLITS.

    // splitLevels[strategy] for ZSTD_optimalBlockSize (index 0 unused;
    // ZSTD_strategy runs fast=1 .. btultra2=9).
    private static readonly int[] SplitLevels = [0, 0, 1, 2, 2, 3, 3, 4, 4, 4];

    /// <summary>
    /// Whether parsed blocks split after parsing
    /// (<c>ZSTD_resolveBlockSplitterMode</c> with default/auto parameters:
    /// enabled exactly for strategy ≥ btopt with windowLog ≥ 17).
    /// </summary>
    internal static bool Enabled(ZstdCompressionParameters prm)
    {
        return prm.Strategy >= ZstdStrategy.BtOpt && prm.WindowLog >= 17;
    }

    // ------------------------------------------------------------------
    // Pre-split: ZSTD_optimalBlockSize + ZSTD_splitBlock (zstd_preSplit.c)
    // ------------------------------------------------------------------

    /// <summary>
    /// Input-block size for the frame-chunk loop
    /// (<c>ZSTD_optimalBlockSize</c>): only full 128 KiB blocks past the
    /// first (tracked by <paramref name="savings"/>, the running
    /// <c>consumed - produced</c> byte balance) are even candidates, and the
    /// fingerprint then decides the cut point. <paramref name="src"/>
    /// holds the whole frame input; the candidate starts at
    /// <paramref name="ip"/> with <paramref name="remaining"/> bytes left.
    /// </summary>
    internal static int OptimalBlockSize(
        ReadOnlySpan<byte> src, int ip, int remaining, int blockMax,
        ZstdStrategy strategy, ref long savings)
    {
        if (remaining < BlockSizeMax || blockMax < BlockSizeMax)
        {
            return Math.Min(remaining, blockMax);
        }

        if (savings < 3)
        {
            return BlockSizeMax;
        }

        var splitLevel = 0; // preBlockSplitter_level default (ZSTD_CCtxParams_init memsets 0).
        if (splitLevel == 1)
        {
            return BlockSizeMax;
        }

        if (splitLevel == 0)
        {
            splitLevel = SplitLevels[(int)strategy];
        }
        else
        {
            splitLevel -= 2;
        }

        return SplitBlock(src, ip, blockMax, splitLevel);
    }

    /// <summary>
    /// Fingerprint cut point for a full 128 KiB block starting at
    /// <paramref name="ip"/> (<c>ZSTD_splitBlock</c>): level 0 compares head
    /// and tail borders, higher levels scan 8 KiB chunks. Returns
    /// <c>blockSize</c> when no split pays off.
    /// </summary>
    internal static int SplitBlock(ReadOnlySpan<byte> src, int ip, int blockSize, int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ip);
        if (level < 0 || level > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (blockSize != BlockSizeMax)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Pre-split only handles full blocks.");
        }

        if (level == 0)
        {
            return SplitFromBorders(src, ip, blockSize);
        }

        return SplitByChunks(src, ip, blockSize, level - 1);
    }

    // ZSTD_splitBlock_fromBorders: fingerprint the first and last 512 bytes;
    // when they differ enough, ask the middle which side it resembles.
    private static int SplitFromBorders(ReadOnlySpan<byte> src, int ip, int blockSize)
    {
        var past = new int[1 << 10];
        var future = new int[1 << 10];
        HistAdd(past, src, ip, SegmentSize);
        HistAdd(future, src, ip + blockSize - SegmentSize, SegmentSize);
        const int nbEvents = SegmentSize;
        if (!CompareFingerprints(past, nbEvents, future, nbEvents, penalty: 0, hashLog: 8))
        {
            return blockSize;
        }

        var middle = new int[1 << 10];
        HistAdd(middle, src, ip + (blockSize / 2) - (SegmentSize / 2), SegmentSize);
        var distFromBegin = FpDistance(past, middle, 8);
        var distFromEnd = FpDistance(future, middle, 8);
        var minDistance = (ulong)SegmentSize * SegmentSize / 3;
        var gap = distFromBegin >= distFromEnd ? distFromBegin - distFromEnd : distFromEnd - distFromBegin;
        if (gap < minDistance)
        {
            return 64 * 1024;
        }

        return distFromBegin > distFromEnd ? 32 * 1024 : 96 * 1024;
    }

    // ZSTD_splitBlock_byChunks: fingerprint successive 8 KiB chunks against
    // the accumulated past; the first "too different" chunk starts a new block.
    private static int SplitByChunks(ReadOnlySpan<byte> src, int ip, int blockSize, int level)
    {
        int[] rates = [43, 11, 5, 1];
        int[] hashLogs = [8, 9, 10, 10];
        var rate = rates[level];
        var hashLog = hashLogs[level];
        var past = new int[1 << 10];
        var fresh = new int[1 << 10];
        var pastEvents = RecordFingerprint(past, src, ip, ChunkSize, rate, hashLog);
        var penalty = 3; // THRESHOLD_PENALTY.
        for (var pos = ChunkSize; pos <= blockSize - ChunkSize; pos += ChunkSize)
        {
            var newEvents = RecordFingerprint(fresh, src, ip + pos, ChunkSize, rate, hashLog);
            if (CompareFingerprints(past, pastEvents, fresh, newEvents, penalty, hashLog))
            {
                return pos;
            }

            MergeEvents(past, fresh, hashLog);
            pastEvents += newEvents;
            if (penalty > 0)
            {
                penalty--;
            }
        }

        return blockSize;
    }

    // HIST_add: byte histogram over [offset, offset + length).
    private static void HistAdd(int[] events, ReadOnlySpan<byte> src, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            events[src[offset + i]]++;
        }
    }

    // hash2: for hashLog 8 the raw byte, else a Knuth-multiplied u16 pair.
    private static uint Hash2(ReadOnlySpan<byte> src, int pos, int hashLog)
    {
        if (hashLog == 8)
        {
            return src[pos];
        }

        var pair = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos, 2));
        return unchecked((uint)(pair * 0x9E3779B9u) >> (32 - hashLog));
    }

    // recordFingerprint_generic over one 8 KiB slice. nbEvents uses the
    // truncated limit/rate quotient exactly like upstream (not the loop trip
    // count, which can be one higher).
    private static int RecordFingerprint(
        int[] events, ReadOnlySpan<byte> src, int offset, int length, int rate, int hashLog)
    {
        Array.Clear(events, 0, 1 << hashLog);
        var limit = length - 2 + 1;
        for (var n = 0; n < limit; n += rate)
        {
            events[Hash2(src, offset + n, hashLog)]++;
        }

        return limit / rate;
    }

    private static void MergeEvents(int[] acc, int[] slice, int hashLog)
    {
        for (var n = 0; n < 1 << hashLog; n++)
        {
            acc[n] += slice[n];
        }
    }

    private static ulong FpDistance(int[] a, int nbA, int[] b, int nbB, int hashLog)
    {
        ulong distance = 0;
        for (var n = 0; n < 1 << hashLog; n++)
        {
            var diff = ((long)a[n] * nbB) - ((long)b[n] * nbA);
            distance += (ulong)(diff < 0 ? -diff : diff);
        }

        return distance;
    }

    private static ulong FpDistance(int[] a, int[] b, int hashLog)
    {
        return FpDistance(a, SegmentSize, b, SegmentSize, hashLog);
    }

    // compareFingerprints: 1 when the spots are "too different".
    private static bool CompareFingerprints(
        int[] reference, int nbRef, int[] candidate, int nbCand, int penalty, int hashLog)
    {
        var p50 = (ulong)nbRef * (ulong)nbCand;
        var deviation = FpDistance(reference, nbRef, candidate, nbCand, hashLog);
        var threshold = (p50 * (ulong)(14 + penalty)) / 16; // THRESHOLD_BASE = 16 - 2.
        return deviation >= threshold;
    }

    // ------------------------------------------------------------------
    // Post-split: ZSTD_compressBlock_splitBlock
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes one splitter-enabled input block: parses the whole block once,
    /// derives sub-block partitions by recursive entropy estimation, and
    /// emits each partition as its own block header + payload
    /// (<c>ZSTD_compressBlock_splitBlock</c>). Only the final partition of a
    /// last input block carries the frame-last flag; raw/RLE partitions keep
    /// the decoder-side history frozen while the compression side always
    /// advances (reconciled through <see cref="ZstdSeq.ResolveOffCodes"/>).
    /// Returns bytes written. A failed parse declines the whole input block
    /// to raw (the caller's <c>ZSTDbss_noCompress</c> equivalent lives in the
    /// tiny-block path; anything else failing here is treated like the
    /// whole-block payload decline).
    /// </summary>
    internal static int WriteSplitBlock(
        ZstdFrameState state, int blockStart, ReadOnlySpan<byte> blockBytes,
        byte[] dst, int pos, int capacity, bool last, uint[] rep, bool isFirstBlock)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rep);
        var strategy = state.Prm.Strategy;

        var startRep = (uint[])rep.Clone();
        var store = new ZstdSequenceStore(Math.Max(1, blockBytes.Length));
        try
        {
            state.FindMatches(blockStart, blockStart + blockBytes.Length, store, rep);
        }
        catch (ZstdException)
        {
            rep[0] = startRep[0];
            rep[1] = startRep[1];
            rep[2] = startRep[2];
            state.DeclineEntropy();
            WriteRawBlock(blockBytes, dst, pos, last);
            return 3 + blockBytes.Length;
        }

        var partitions = DeriveSplits(store, state.Entropy, strategy);
        var numSplits = partitions.Count - 1;
        var isPartition = numSplits > 0;

        var dRep = (uint[])startRep.Clone();
        var cRep = (uint[])startRep.Clone();
        var outPos = pos;
        var capLeft = capacity;
        var srcPos = 0;
        var srcBytesTotal = 0;
        for (var i = 0; i < partitions.Count; i++)
        {
            var (start, end) = partitions[i];
            var lastPartition = i == numSplits;
            var chunk = store.Slice(start, end, lastPartition);
            var srcBytes = CountChunkBytes(chunk);
            srcBytesTotal += srcBytes;
            if (lastPartition)
            {
                // The final partition absorbs the block's trailing literals.
                srcBytes += blockBytes.Length - srcBytesTotal;
            }

            var slice = blockBytes.Slice(srcPos, srcBytes);
            if (isPartition)
            {
                var longLitIdx = chunk.LongLengthType == 1
                    ? chunk.LongLengthPos
                    : chunk.Count;
                ZstdSeq.ResolveOffCodes(dRep, cRep, chunk, chunk.Count, longLitIdx);
            }

            var dEntry = (uint[])dRep.Clone();
            int payload;
            try
            {
                payload = ZstdBlockEncoder.EncodeStoreStateful(
                    state, chunk, dst, outPos + 3, outPos + capLeft);
            }
            catch (ZstdException)
            {
                payload = -1;
            }

            int written;
            var maxCSize = srcBytes - ZstdBlockEncoder.MinGain(srcBytes, strategy);
            if (payload < 0 || payload >= maxCSize || payload > ((1 << 21) - 1))
            {
                Array.Copy(dEntry, dRep, 3);
                state.DeclineEntropy();
                WriteRawBlock(slice, dst, outPos, last && lastPartition);
                written = 3 + srcBytes;
            }
            else if (!isFirstBlock && payload < ZstdCompressor.RleMaxLength && ZstdCompressor.IsUniform(slice))
            {
                Array.Copy(dEntry, dRep, 3);
                state.DeclineEntropy();
                if (capLeft < 4)
                {
                    throw new ZstdException("Frame destination too small.");
                }

                var header = (last && lastPartition ? 1u : 0u) | (1u << 1) | ((uint)srcBytes << 3);
                dst[outPos] = (byte)header;
                dst[outPos + 1] = (byte)(header >> 8);
                dst[outPos + 2] = (byte)(header >> 16);
                dst[outPos + 3] = slice[0];
                written = 4;
            }
            else
            {
                state.ConfirmEntropy();
                var header = (last && lastPartition ? 1u : 0u) | (2u << 1) | ((uint)payload << 3);
                dst[outPos] = (byte)header;
                dst[outPos + 1] = (byte)(header >> 8);
                dst[outPos + 2] = (byte)(header >> 16);
                written = 3 + payload;
            }

            srcPos += srcBytes;
            outPos += written;
            capLeft -= written;
        }

        if (isPartition)
        {
            // The decompression-side history wins for the next input block
            // (native overwrites prevCBlock->rep with dRep after the loop).
            rep[0] = dRep[0];
            rep[1] = dRep[1];
            rep[2] = dRep[2];
        }

        return outPos - pos;
    }

    private static void WriteRawBlock(ReadOnlySpan<byte> chunk, byte[] dst, int pos, bool last)
    {
        if (pos + 3 + chunk.Length > dst.Length)
        {
            throw new ZstdException("Frame destination too small.");
        }

        var header = (last ? 1u : 0u) | ((uint)chunk.Length << 3);
        dst[pos] = (byte)header;
        dst[pos + 1] = (byte)(header >> 8);
        dst[pos + 2] = (byte)(header >> 16);
        chunk.CopyTo(new Span<byte>(dst, pos + 3, chunk.Length));
    }

    // ZSTD_countSeqStoreLiteralsBytes + ZSTD_countSeqStoreMatchBytes over a
    // derived chunk (resolved lengths, long-length aware via Get).
    private static int CountChunkBytes(ZstdSequenceStore chunk)
    {
        var total = 0;
        for (var i = 0; i < chunk.Count; i++)
        {
            var seq = chunk.Get(i);
            total += (int)seq.LitLength + (int)seq.MatchLength;
        }

        return total;
    }

    // ------------------------------------------------------------------
    // Split search: ZSTD_deriveBlockSplits{,Helper}
    // ------------------------------------------------------------------

    /// <summary>
    /// Partition boundaries as sequence-index ranges
    /// (<c>ZSTD_deriveBlockSplits</c>): recursive halving while the estimated
    /// halves beat the estimated whole; runs of ≤ 4 sequences never split.
    /// Returns at least one range covering <c>[0, store.Count)</c>.
    /// </summary>
    internal static List<(int Start, int End)> DeriveSplits(
        ZstdSequenceStore store, ZstdEntropyState prev, ZstdStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prev);
        var nbSeq = store.Count;
        var splits = new List<int>();
        if (nbSeq > 4)
        {
            DeriveSplitsHelper(store, prev, strategy, splits, 0, nbSeq);
        }

        splits.Add(nbSeq);
        var ranges = new List<(int Start, int End)>(splits.Count);
        var start = 0;
        foreach (var split in splits)
        {
            ranges.Add((start, split));
            start = split;
        }

        return ranges;
    }

    private static void DeriveSplitsHelper(
        ZstdSequenceStore store, ZstdEntropyState prev, ZstdStrategy strategy,
        List<int> splits, int startIdx, int endIdx)
    {
        if (endIdx - startIdx < MinSequencesSplit || splits.Count >= MaxBlockSplits)
        {
            return;
        }

        var nbSeq = store.Count;
        var midIdx = (startIdx + endIdx) / 2;
        long whole, first, second;
        try
        {
            whole = EstimateChunk(store.Slice(startIdx, endIdx, endIdx == nbSeq), prev, strategy);
            first = EstimateChunk(store.Slice(startIdx, midIdx, false), prev, strategy);
            second = EstimateChunk(store.Slice(midIdx, endIdx, endIdx == nbSeq), prev, strategy);
        }
        catch (ZstdException)
        {
            // An un-estimable chunk (native: ZSTD_isError estimate) refuses
            // the split at this node.
            return;
        }

        if (first + second < whole)
        {
            DeriveSplitsHelper(store, prev, strategy, splits, startIdx, midIdx);
            splits.Add(midIdx);
            DeriveSplitsHelper(store, prev, strategy, splits, midIdx, endIdx);
        }
    }

    // ------------------------------------------------------------------
    // Estimation: ZSTD_buildEntropyStatisticsAndEstimateSubBlockSize
    // ------------------------------------------------------------------

    /// <summary>
    /// Estimated compressed size of one chunk store
    /// (<c>ZSTD_buildEntropyStatisticsAndEstimateSubBlockSize</c>): builds
    /// the entropy statistics the real writer would select, then prices the
    /// chunk with <c>ZSTD_estimateBlockSize</c> (header + streams + table
    /// descriptions, real tables staged but never emitted).
    /// </summary>
    internal static long EstimateChunk(
        ZstdSequenceStore chunk, ZstdEntropyState prev, ZstdStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(prev);
        var nbSeq = chunk.Count;
        var litSize = chunk.LiteralLength + chunk.TrailingLength;
        var litBytes = new byte[litSize];
        chunk.Literals.CopyTo(new Span<byte>(litBytes, 0, chunk.LiteralLength));
        chunk.TrailingLiterals.CopyTo(new Span<byte>(litBytes, chunk.LiteralLength, chunk.TrailingLength));

        var huff = BuildHuffmanStats(litBytes, prev.HufTable, prev.HufRepeat, strategy);

        int llType, ofType, mlType;
        ZstdBlockEncoder.SeqAlphabet ll, of, ml;
        long fseTablesSize;
        byte[] llCodes = [], ofCodes = [], mlCodes = [];
        if (nbSeq == 0)
        {
            // Dummy statistics (ZSTD_buildDummySequencesStatistics): every
            // alphabet basic with cleared repeat modes.
            llType = ofType = mlType = 0;
            ll = DummyAlphabet();
            of = DummyAlphabet();
            ml = DummyAlphabet();
            fseTablesSize = 0;
        }
        else
        {
            var codes = ZstdBlockEncoder.DeriveCodes(chunk, nbSeq);
            llCodes = codes.Ll;
            ofCodes = codes.Of;
            mlCodes = codes.Ml;
            var llRepeat = prev.LlRepeat;
            var ofRepeat = prev.OfRepeat;
            var mlRepeat = prev.MlRepeat;
            ll = ZstdBlockEncoder.BuildSeqTable(
                (uint[])codes.LlCount.Clone(), llCodes, nbSeq, codes.LlMax,
                ZstdBlockEncoder.LlFseLog, ZstdBlockEncoder.LlDefaultNorm,
                ZstdBlockEncoder.LlDefaultNormLog, ZstdBlockEncoder.MaxLl,
                defaultAllowed: true, strategy, prev.LlTable, ref llRepeat);
            of = ZstdBlockEncoder.BuildSeqTable(
                (uint[])codes.OfCount.Clone(), ofCodes, nbSeq, codes.OfMax,
                ZstdBlockEncoder.OffFseLog, ZstdBlockEncoder.OfDefaultNorm,
                ZstdBlockEncoder.OfDefaultNormLog, ZstdBlockEncoder.DefaultMaxOff,
                defaultAllowed: codes.OfMax <= ZstdBlockEncoder.DefaultMaxOff,
                strategy, prev.OfTable, ref ofRepeat);
            ml = ZstdBlockEncoder.BuildSeqTable(
                (uint[])codes.MlCount.Clone(), mlCodes, nbSeq, codes.MlMax,
                ZstdBlockEncoder.MlFseLog, ZstdBlockEncoder.MlDefaultNorm,
                ZstdBlockEncoder.MlDefaultNormLog, ZstdBlockEncoder.MaxMl,
                defaultAllowed: true, strategy, prev.MlTable, ref mlRepeat);
            // Section encoding types (set_basic/rle/compressed/repeat), not
            // the repeat modes: the estimator prices the emitted form.
            llType = ll.Mode;
            ofType = of.Mode;
            mlType = ml.Mode;
            fseTablesSize = NCountSize(ll) + NCountSize(of) + NCountSize(ml);
        }

        var litEstimate = EstimateLiterals(
            litSize, huff.Type, huff.Table, huff.DesSize, huff.Count, huff.MaxSymbolValue);
        var seqEstimate = EstimateSequences(
            ofCodes, llCodes, mlCodes, nbSeq,
            ofType, llType, mlType, of.Table, ll.Table, ml.Table, fseTablesSize);
        return litEstimate + seqEstimate + ZstdBlockEncoder.BlockHeaderSize;
    }

    private static ZstdBlockEncoder.SeqAlphabet DummyAlphabet()
    {
        // Basic mode carries no table; the estimator never touches it.
        return new ZstdBlockEncoder.SeqAlphabet(null!, 0, 0, null, 0);
    }

    // Table-description bytes one alphabet contributes to fseTablesSize
    // (basic/repeat contribute none; RLE contributes its symbol byte, which
    // native's ZSTD_buildCTable writes into the stats buffer and counts —
    // the estimate must include it even though the writer emits it later).
    private static long NCountSize(ZstdBlockEncoder.SeqAlphabet alphabet)
    {
        if (alphabet.Mode == ZstdBlockEncoder.SeqModeRle)
        {
            return 1;
        }

        if (alphabet.Mode != ZstdBlockEncoder.SeqModeFse || alphabet.Norm is null)
        {
            return 0;
        }

        var scratch = new byte[ZstdFseEncoder.NCountBound];
        return ZstdFseEncoder.WriteNCount(
            scratch, 0, scratch.Length, alphabet.Norm, alphabet.MaxObserved, alphabet.TableLog);
    }

    /// <summary>
    /// Huffman statistics for one literal run
    /// (<c>ZSTD_buildBlockEntropyStats_literals</c>): the mode, the table
    /// description size, and the staged table the estimator prices with.
    /// Unlike the real compressor this never emits streams and knows no
    /// prefer-repeat shortcut or suspect sampling — exactly like upstream,
    /// whose statistics builder also skips both.
    /// </summary>
    internal readonly record struct HuffmanStats(
        int Type, int DesSize, HuffmanCTable? Table, uint[] Count, int MaxSymbolValue);

    internal static HuffmanStats BuildHuffmanStats(
        byte[] litBytes, HuffmanCTable? prevTable, ZstdHufRepeat prevRepeat, ZstdStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(litBytes);
        var litSize = litBytes.Length;
        var count = new uint[ZstdHuffmanEncoder.SymbolValueMax + 1];
        for (var i = 0; i < litSize; i++)
        {
            count[litBytes[i]]++;
        }

        var maxSv = ZstdHuffmanEncoder.SymbolValueMax;
        while (maxSv > 0 && count[maxSv] == 0)
        {
            maxSv--;
        }

        uint largest = 0;
        for (var i = 0; i <= maxSv; i++)
        {
            largest = Math.Max(largest, count[i]);
        }

        // Small inputs and single-symbol / flat distributions never build a
        // table (COMPRESS_LITERALS_SIZE_MIN / RLE / no-gain heuristics).
        if (litSize <= (prevRepeat == ZstdHufRepeat.Valid ? 6 : 63))
        {
            return new HuffmanStats(0, 0, prevTable, count, maxSv);
        }

        if (largest == (uint)litSize)
        {
            return new HuffmanStats(1, 0, prevTable, count, maxSv);
        }

        if (largest <= (uint)(litSize >> 7) + 4)
        {
            return new HuffmanStats(0, 0, prevTable, count, maxSv);
        }

        var repeat = prevRepeat;
        if (repeat == ZstdHufRepeat.Check
            && (prevTable is null || !ZstdHuffmanEncoder.ValidateCTable(prevTable, count, maxSv)))
        {
            repeat = ZstdHufRepeat.None;
        }

        var tableLog = strategy >= ZstdStrategy.BtUltra
            ? ZstdHuffmanEncoder.OptimalTableLogDepth(count, maxSv, ZstdHuffmanEncoder.TableLogDefault)
            : ZstdHuffmanEncoder.OptimalTableLog(litSize, maxSv, ZstdHuffmanEncoder.TableLogDefault);
        var (table, maxBits) = ZstdHuffmanEncoder.BuildCTable(count, maxSv, tableLog);
        _ = maxBits;
        var newCSize = ZstdHuffmanEncoder.EstimateCompressedSize(table, count, maxSv);
        var scratch = new byte[ZstdHuffmanEncoder.CTableBound * 2];
        var hSize = ZstdHuffmanEncoder.WriteCTable(scratch, 0, scratch.Length, table);

        // An unwritable description (native: error-sized hSize) repeats a
        // usable old table, else declines to basic.
        if (hSize <= 0)
        {
            if (repeat != ZstdHufRepeat.None && prevTable is not null
                && ZstdHuffmanEncoder.EstimateCompressedSize(prevTable, count, maxSv) < litSize)
            {
                return new HuffmanStats(3, 0, prevTable, count, maxSv);
            }

            return new HuffmanStats(0, 0, prevTable, count, maxSv);
        }

        if (repeat != ZstdHufRepeat.None && prevTable is not null)
        {
            var oldCSize = ZstdHuffmanEncoder.EstimateCompressedSize(prevTable, count, maxSv);
            if (oldCSize < litSize && (oldCSize <= hSize + newCSize || hSize + 12 >= litSize))
            {
                return new HuffmanStats(3, 0, prevTable, count, maxSv);
            }
        }

        if (newCSize + hSize >= litSize)
        {
            return new HuffmanStats(0, 0, prevTable, count, maxSv);
        }

        return new HuffmanStats(2, hSize, table, count, maxSv);
    }

    // ZSTD_estimateBlockSize_literal (types: 0 basic, 1 rle, 2 compressed,
    // 3 repeat). The header is always 3-based here (never the 200-byte
    // superblock slack); multi-stream estimates add the 6-byte jump table.
    private static long EstimateLiterals(
        int litSize, int hType, HuffmanCTable? staged,
        int hDesSize, uint[] count, int maxSv)
    {
        if (hType == 0)
        {
            return litSize;
        }

        if (hType == 1)
        {
            return 1;
        }

        var estimate = ZstdHuffmanEncoder.EstimateCompressedSize(staged!, count, maxSv);
        if (hType == 2)
        {
            estimate += hDesSize;
        }

        if (litSize >= 256)
        {
            estimate += 6;
        }

        return estimate + 3 + (litSize >= 1024 ? 1 : 0) + (litSize >= 16384 ? 1 : 0);
    }

    // ZSTD_estimateBlockSize_sequences with writeSeqEntropy always set (the
    // split search prices table descriptions every time).
    private static long EstimateSequences(
        byte[] ofCodes, byte[] llCodes, byte[] mlCodes, int nbSeq,
        int ofType, int llType, int mlType,
        FseCTable? ofTable, FseCTable? llTable, FseCTable? mlTable,
        long fseTablesSize)
    {
        var estimate = EstimateSymbol(ofType, ofCodes, nbSeq, ZstdBlockEncoder.MaxOff,
                ofTable, additionalBits: null,
                ZstdBlockEncoder.OfDefaultNorm, ZstdBlockEncoder.OfDefaultNormLog,
                ZstdBlockEncoder.DefaultMaxOff)
            + EstimateSymbol(llType, llCodes, nbSeq, ZstdBlockEncoder.MaxLl,
                llTable, ZstdBlockEncoder.LlExtraBits,
                ZstdBlockEncoder.LlDefaultNorm, ZstdBlockEncoder.LlDefaultNormLog,
                ZstdBlockEncoder.MaxLl)
            + EstimateSymbol(mlType, mlCodes, nbSeq, ZstdBlockEncoder.MaxMl,
                mlTable, ZstdBlockEncoder.MlExtraBits,
                ZstdBlockEncoder.MlDefaultNorm, ZstdBlockEncoder.MlDefaultNormLog,
                ZstdBlockEncoder.MaxMl);
        estimate += fseTablesSize;
        return estimate + 1 + 1 + (nbSeq >= 128 ? 1 : 0) + (nbSeq >= ZstdBlockEncoder.LongNbSeq ? 1 : 0);
    }

    // ZSTD_estimateBlockSize_symbolType: cross-entropy for basic, nothing for
    // RLE, FSE bit cost for compressed/repeat (nbSeq * 10 on error), plus the
    // per-symbol extra bits (the offset code itself for offsets).
    private static long EstimateSymbol(
        int type, byte[] codes, int nbSeq, int maxCode, FseCTable? table,
        Func<int, byte>? additionalBits, short[] defaultNorm, int defaultNormLog, int defaultMax)
    {
        ulong bits;
        if (type == ZstdBlockEncoder.SeqModeBasic)
        {
            var count = new uint[maxCode + 1];
            var max = maxCode;
            CountCodes(count, codes, nbSeq, ref max);
            bits = ZstdBlockEncoder.CrossEntropyCost(defaultNorm, (uint)defaultNormLog, count, (uint)max);
        }
        else if (type == ZstdBlockEncoder.SeqModeRle)
        {
            bits = 0;
        }
        else
        {
            var count = new uint[maxCode + 1];
            var max = maxCode;
            CountCodes(count, codes, nbSeq, ref max);
            bits = ZstdFseEncoder.FseBitCost(table!, count, max);
            if (bits == ulong.MaxValue)
            {
                return (long)nbSeq * 10;
            }
        }

        for (var i = 0; i < nbSeq; i++)
        {
            bits += additionalBits is not null ? additionalBits(codes[i]) : codes[i];
        }

        return (long)(bits >> 3);
    }

    private static void CountCodes(uint[] count, byte[] codes, int nbSeq, ref int max)
    {
        var observed = 0;
        for (var i = 0; i < nbSeq; i++)
        {
            count[codes[i]]++;
            observed = Math.Max(observed, codes[i]);
        }

        // HIST_countFast_wksp reports the observed maximum, like upstream.
        max = observed;
    }
}
