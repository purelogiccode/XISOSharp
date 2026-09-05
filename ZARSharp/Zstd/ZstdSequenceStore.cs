using System.Runtime.InteropServices;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace ZARSharp.Zstd;

/// <summary>
/// Sequence-code helpers shared by the match finder (Phase 4) and the block
/// encoder (Phase 5). Ports the <c>offBase</c> sum-type macros and
/// <c>ZSTD_updateRep</c> from <c>lib/compress/zstd_compress_internal.h:718-848</c>.
/// <para/>
/// An <c>offBase</c> is either a repeat code (<c>1..3</c>, see RFC 8878 §4.1.1)
/// or a real offset plus <see cref="RepNum"/> (<c>OFFSET_TO_OFFBASE</c>).
/// Dictionary-mode paths are deleted: the window always starts empty and all
/// matches are bounded by the current block (prefix only).
/// </summary>
public static class ZstdSeq
{
    /// <summary>Minimum match length (<c>MINMATCH</c>, <c>lib/common/zstd_internal.h:98</c>).</summary>
    public const int MinMatch = 3;

    /// <summary>Number of repeat-offset codes (<c>ZSTD_REP_NUM</c>).</summary>
    public const int RepNum = 3;

    /// <summary>Repeat code 1 (<c>REPCODE1_TO_OFFBASE</c>).</summary>
    public const uint Repcode1 = 1;

    /// <summary>Repeat code 2 (<c>REPCODE2_TO_OFFBASE</c>).</summary>
    public const uint Repcode2 = 2;

    /// <summary>Repeat code 3 (<c>REPCODE3_TO_OFFBASE</c>).</summary>
    public const uint Repcode3 = 3;

    /// <summary>Initial repeat offsets at the start of a frame (<c>repStartValue = {1,4,8}</c>).</summary>
    public static uint[] FreshRepeatOffsets()
    {
        return [1, 4, 8];
    }

    /// <summary><c>OFFSET_TO_OFFBASE(o)</c>: encodes a real offset (must be ≥ 1).</summary>
    public static uint OffsetToOffBase(uint offset)
    {
        return offset > 0
            ? offset + RepNum
            : throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be >= 1.");
    }

    /// <summary><c>OFFBASE_IS_OFFSET(o)</c>.</summary>
    public static bool IsOffset(uint offBase)
    {
        return offBase > RepNum;
    }

    /// <summary><c>OFFBASE_IS_REPCODE(o)</c>.</summary>
    public static bool IsRepcode(uint offBase)
    {
        return offBase >= 1 && offBase <= RepNum;
    }

    /// <summary><c>OFFBASE_TO_OFFSET(o)</c>: decodes a real offset (must be an offset code).</summary>
    public static uint ToOffset(uint offBase)
    {
        return IsOffset(offBase)
            ? offBase - RepNum
            : throw new ArgumentOutOfRangeException(nameof(offBase), "Not an offset code.");
    }

    /// <summary><c>OFFBASE_TO_REPCODE(o)</c>: decodes a repeat code id 1..3.</summary>
    public static uint ToRepcode(uint offBase)
    {
        return IsRepcode(offBase)
            ? offBase
            : throw new ArgumentOutOfRangeException(nameof(offBase), "Not a repeat code.");
    }

    /// <summary>
    /// <c>ZSTD_newRep</c>: copies <paramref name="rep"/> and applies
    /// <see cref="UpdateRep"/>, returning the new history (the input is left
    /// untouched).
    /// </summary>
    public static uint[] NewRep(uint[] rep, uint offBase, uint litLengthZero)
    {
        ArgumentNullException.ThrowIfNull(rep);
        var fresh = (uint[])rep.Clone();
        UpdateRep(fresh, offBase, litLengthZero);
        return fresh;
    }

    /// <summary>
    /// <c>ZSTD_updateRep</c>: updates the 3-entry repeat-offset history in place
    /// after storing a sequence with the given <c>offBase</c>.
    /// <paramref name="litLengthZero"/> is 1 when the sequence's literal length
    /// is 0, else 0 (the <c>ll0</c> parameter).
    /// </summary>
    public static void UpdateRep(uint[] rep, uint offBase, uint litLengthZero)
    {
        ArgumentNullException.ThrowIfNull(rep);
        if (rep.Length < RepNum)
        {
            throw new ArgumentException("Repeat history needs 3 entries.", nameof(rep));
        }

        if (IsOffset(offBase))
        {
            rep[2] = rep[1];
            rep[1] = rep[0];
            rep[0] = ToOffset(offBase);
        }
        else
        {
            // REPCODE_TO_OFFBASE values are 1..3; ToRepcode validates.
            var repCode = ToRepcode(offBase) - 1 + litLengthZero;
            if (repCode > 0)
            {
                var currentOffset = repCode == RepNum
                    ? checked(rep[0] - 1)
                    : rep[repCode];
                rep[2] = repCode >= 2 ? rep[1] : rep[2];
                rep[1] = rep[0];
                rep[0] = currentOffset;
            }
        }
    }

    /// <summary>
    /// Raw offset a repeat code refers to under <paramref name="rep"/>
    /// (<c>ZSTD_resolveRepcodeToRawOffset</c>,
    /// <c>lib/compress/zstd_compress.c</c>): repcodes are 1-based ids into the
    /// history shifted by <paramref name="ll0"/>, with the id-3/ll0-1 slot
    /// meaning first-history-minus-one (0 when the history holds 1 — an
    /// invalid offset the caller compares away and discards).
    /// </summary>
    internal static uint ResolveRepcodeToRawOffset(uint[] rep, uint offBase, uint ll0)
    {
        ArgumentNullException.ThrowIfNull(rep);
        var adjusted = ToRepcode(offBase) - 1 + ll0; // [0..3].
        if (adjusted == RepNum)
        {
            return unchecked(rep[0] - 1);
        }

        return rep[adjusted];
    }

    /// <summary>
    /// Reconciles offset histories split by a raw/RLE partition
    /// (<c>ZSTD_seqStore_resolveOffCodes</c>): walks the chunk's sequences with
    /// decompression-side (<paramref name="dRep"/>) and compression-side
    /// (<paramref name="cRep"/>) histories; a repcode resolving differently on
    /// the two sides is replaced in the store by the raw offset the
    /// compression side meant. <paramref name="longLitLenIdx"/> is the
    /// chunk-relative long-literal position (or <paramref name="nbSeq"/> when
    /// the chunk has no long literal), which forces <c>ll0</c> off there like
    /// upstream.
    /// </summary>
    internal static void ResolveOffCodes(
        uint[] dRep, uint[] cRep, ZstdSequenceStore store, int nbSeq, int longLitLenIdx)
    {
        ArgumentNullException.ThrowIfNull(dRep);
        ArgumentNullException.ThrowIfNull(cRep);
        ArgumentNullException.ThrowIfNull(store);
        for (var idx = 0; idx < nbSeq; idx++)
        {
            var seq = store.Get(idx);
            var ll0 = (seq.LitLength == 0 ? 1u : 0u) & (idx != longLitLenIdx ? 1u : 0u);
            var offBase = seq.OffBase;
            var original = offBase;
            if (IsRepcode(offBase))
            {
                var dRaw = ResolveRepcodeToRawOffset(dRep, offBase, ll0);
                var cRaw = ResolveRepcodeToRawOffset(cRep, offBase, ll0);
                if (dRaw != cRaw)
                {
                    // OFFSET_TO_OFFBASE without the >0 assert: a zero raw
                    // offset (first-history 1 minus one) wraps to a repcode
                    // id in release C, and is compared away downstream.
                    offBase = unchecked(cRaw + RepNum);
                    store.SetOffBase(idx, offBase);
                }
            }

            // Decompression history follows the (possibly replaced) stored
            // value; compression history follows the original sequence.
            UpdateRep(dRep, offBase, ll0);
            UpdateRep(cRep, original, ll0);
        }
    }
}

/// <summary>
/// One parsed sequence with resolved lengths: literal run length, offset base
/// code (repeat 1..3 or real offset + 3), and full match length (with
/// <c>MINMATCH</c> added back and any long-length extension applied).
/// </summary>
/// <param name="LitLength">Literal run length in bytes.</param>
/// <param name="OffBase">Offset base code (see <see cref="ZstdSeq"/>).</param>
/// <param name="MatchLength">Full match length in bytes (≥ <see cref="ZstdSeq.MinMatch"/>).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ZstdSequence(uint LitLength, uint OffBase, uint MatchLength);

/// <summary>
/// Parsed-sequence storage for one block. Ports <c>SeqStore_t</c> and
/// <c>ZSTD_storeSeq</c> / <c>ZSTD_storeSeqOnly</c> from
/// <c>lib/compress/zstd_compress_internal.h:85-140,728-811</c>, minus the
/// entropy-code buffers (<c>llCode/mlCode/ofCode</c>, Phase 5) and minus
/// dictionary support.
/// <para/>
/// Literals are copied into an owned buffer (like upstream); trailing literals
/// (the final run after the last match) are appended after the sequence
/// literals via <see cref="SetTrailingLiterals"/>.
/// Lengths above 0xFFFF use upstream's single-long-length rule
/// (<c>longLengthType/longLengthPos</c>, +0x10000); blocks here are ≤ 64 KiB so
/// it never triggers, but the rule is enforced.
/// </summary>
public sealed class ZstdSequenceStore
{
    private const int LongLengthNone = 0;
    private const int LongLengthLiteral = 1;
    private const int LongLengthMatch = 2;
    private const int LongLengthAdd = 0x10000;

    private uint[] _offBases;
    private ushort[] _litLengths;
    private ushort[] _mlBases;

    private byte[] _literals;
    private bool _trailingSet;

    private int _longLengthType;
    private int _longLengthPos;

    /// <summary>Creates a store pre-sized for a source of <paramref name="maxSourceSize"/> bytes.</summary>
    public ZstdSequenceStore(int maxSourceSize = 65536)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSourceSize);
        // Every sequence consumes at least MinMatch... in practice ≥ 4 bytes
        // (both finders); bound generously and grow on demand regardless.
        var seqCap = Math.Max(4, (maxSourceSize / ZstdSeq.MinMatch) + 2);
        _offBases = new uint[seqCap];
        _litLengths = new ushort[seqCap];
        _mlBases = new ushort[seqCap];
        _literals = new byte[Math.Max(1, maxSourceSize)];
    }

    /// <summary>Number of stored sequences.</summary>
    public int Count { get; private set; }

    /// <summary>Total bytes of sequence literals (excludes trailing literals).</summary>
    public int LiteralLength { get; private set; }

    /// <summary>Sequence literals.</summary>
    public ReadOnlySpan<byte> Literals => new(_literals, 0, LiteralLength);

    /// <summary>Trailing literal count (0 until <see cref="SetTrailingLiterals"/>).</summary>
    public int TrailingLength { get; private set; }

    /// <summary>Trailing literals (final run after the last match).</summary>
    public ReadOnlySpan<byte> TrailingLiterals => new(_literals, LiteralLength, TrailingLength);

    /// <summary>
    /// Stores one sequence: copies <paramref name="literals"/> then appends
    /// (<c>offBase</c>, <c>matchLength</c>). Mirrors <c>ZSTD_storeSeq</c>.
    /// </summary>
    /// <exception cref="ZstdException">On a second long length (upstream asserts).</exception>
    public void StoreSequence(ReadOnlySpan<byte> literals, uint offBase, int matchLength)
    {
        if (offBase < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(offBase), "offBase 0 is invalid.");
        }

        if (matchLength < ZstdSeq.MinMatch)
        {
            throw new ArgumentOutOfRangeException(nameof(matchLength), "Match length must be >= MINMATCH.");
        }

        EnsureSequenceCapacity();
        EnsureLiteralCapacity(LiteralLength + literals.Length);
        literals.CopyTo(new Span<byte>(_literals, LiteralLength, literals.Length));
        LiteralLength += literals.Length;

        StoreSequenceOnly((uint)literals.Length, offBase, matchLength);
    }

    /// <summary>
    /// Sets the trailing literals (final run after the last match). Called once
    /// per block by the match finder. Mirrors the <c>lastLits</c> return value
    /// of <c>ZSTD_compressBlock_*</c>.
    /// </summary>
    public void SetTrailingLiterals(ReadOnlySpan<byte> trailing)
    {
        if (_trailingSet)
        {
            throw new ZstdException("Trailing literals already set.");
        }

        _trailingSet = true;
        EnsureLiteralCapacity(LiteralLength + trailing.Length);
        trailing.CopyTo(new Span<byte>(_literals, LiteralLength, trailing.Length));
        TrailingLength = trailing.Length;
    }

    /// <summary>
    /// Returns sequence <paramref name="index"/> with resolved lengths
    /// (long-length +0x10000 applied, <c>MINMATCH</c> added back to the match).
    /// Mirrors <c>ZSTD_getSequenceLength</c>.
    /// </summary>
    public ZstdSequence Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        uint litLength = _litLengths[index];
        var matchLength = (uint)(_mlBases[index] + ZstdSeq.MinMatch);
        if (index == _longLengthPos)
        {
            if (_longLengthType == LongLengthLiteral)
            {
                litLength += LongLengthAdd;
            }
            else if (_longLengthType == LongLengthMatch)
            {
                matchLength += LongLengthAdd;
            }
        }

        return new ZstdSequence(litLength, _offBases[index], matchLength);
    }

    /// <summary>Clears all sequences, literals, and long-length state for reuse.</summary>
    public void Reset()
    {
        Count = 0;
        LiteralLength = 0;
        TrailingLength = 0;
        _trailingSet = false;
        _longLengthType = LongLengthNone;
        _longLengthPos = 0;
    }

    /// <summary>
    /// Long-length kind of this store (<c>longLengthType</c>: 0 none, 1 literal,
    /// 2 match). Needed for the splitter's repcode resolution
    /// (<c>longLitLenIdx</c> in <c>ZSTD_seqStore_resolveOffCodes</c>).
    /// </summary>
    internal int LongLengthType => _longLengthType;

    /// <summary>Chunk-relative long-length position (<c>longLengthPos</c>).</summary>
    internal int LongLengthPos => _longLengthPos;

    /// <summary>
    /// Overwrites the offset base of sequence <paramref name="index"/>
    /// (the splitter's repcode-resolution rewrite,
    /// <c>ZSTD_seqStore_resolveOffCodes</c>).
    /// </summary>
    internal void SetOffBase(int index, uint offBase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        if (offBase < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(offBase), "offBase 0 is invalid.");
        }

        _offBases[index] = offBase;
    }

    /// <summary>
    /// Derives the chunk store for sequences
    /// <c>[startSeq, endSeq)</c> (<c>ZSTD_deriveSeqStoreChunk</c>): sequence
    /// metadata and literals are copied; the trailing literals travel only
    /// with the final chunk (<paramref name="isFinal"/>), earlier chunks end
    /// exactly at their sequences' literals. The long-length marker moves
    /// with its sequence (chunk-relative) or clears when outside.
    /// </summary>
    internal ZstdSequenceStore Slice(int startSeq, int endSeq, bool isFinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSeq);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endSeq, Count);
        if (endSeq < startSeq)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeq));
        }

        var chunk = new ZstdSequenceStore(Math.Max(1, endSeq - startSeq));
        var litPos = 0;
        for (var i = 0; i < startSeq; i++)
        {
            litPos += (int)Get((int)i).LitLength;
        }

        var seqLits = new ReadOnlySpan<byte>(_literals, 0, LiteralLength);
        for (var i = startSeq; i < endSeq; i++)
        {
            var seq = Get(i);
            var litLen = (int)seq.LitLength;
            // Match length round-trips through StoreSequenceOnly's u16 +
            // long-length rule, exactly like the original parse.
            chunk.StoreSequence(
                seqLits.Slice(litPos, litLen), seq.OffBase, (int)seq.MatchLength);
            litPos += litLen;
        }

        if (isFinal)
        {
            chunk.SetTrailingLiterals(TrailingLiterals.ToArray());
        }
        else
        {
            chunk.SetTrailingLiterals([]);
        }

        return chunk;
    }

    private void StoreSequenceOnly(uint litLength, uint offBase, int matchLength)
    {
        // Mirrors ZSTD_storeSeqOnly, including U16 truncation + long-length rule.
        if (litLength > 0xFFFF)
        {
            if (_longLengthType != LongLengthNone)
            {
                throw new ZstdException("Only a single long length is allowed per block.");
            }

            _longLengthType = LongLengthLiteral;
            _longLengthPos = Count;
        }

        _litLengths[Count] = (ushort)litLength;
        _offBases[Count] = offBase;

        var mlBase = (long)matchLength - ZstdSeq.MinMatch;
        if (mlBase > 0xFFFF)
        {
            if (_longLengthType != LongLengthNone)
            {
                throw new ZstdException("Only a single long length is allowed per block.");
            }

            _longLengthType = LongLengthMatch;
            _longLengthPos = Count;
        }

        _mlBases[Count] = (ushort)mlBase;
        Count++;
    }

    private void EnsureSequenceCapacity()
    {
        if (Count < _offBases.Length)
        {
            return;
        }

        var next = _offBases.Length * 2;
        Array.Resize(ref _offBases, next);
        Array.Resize(ref _litLengths, next);
        Array.Resize(ref _mlBases, next);
    }

    private void EnsureLiteralCapacity(int needed)
    {
        if (needed <= _literals.Length)
        {
            return;
        }

        var next = Math.Max(needed, _literals.Length * 2);
        Array.Resize(ref _literals, next);
    }
}