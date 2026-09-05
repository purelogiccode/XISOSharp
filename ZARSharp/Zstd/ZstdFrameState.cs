namespace ZARSharp.Zstd;

/// <summary>
/// Per-frame matchfinder state for multi-block frames: the full input plus
/// the match tables that persist across the frame's 128 KiB blocks, exactly
/// like <c>ZSTD_MatchState_t</c> (no dictionaries, single-shot, contiguous).
/// Positions are absolute frame offsets; each block parses
/// <c>[blockStart, blockEnd)</c> with its anchor reset to
/// <c>blockStart</c> while matches may reference any earlier frame data
/// within the window. Tables are allocated once from the frame-level
/// (total-size, adjusted) row and start zeroed, so the first block parses
/// exactly like a fresh single-shot block.
/// <para/>
/// Scope note: the window never slides in practice here (inputs are far
/// below the adjusted window size, so the lowest valid match index stays 0
/// and overflow correction cannot trigger); the engines therefore keep their
/// validated zero-based bounds, which are already absolute-safe. Larger
/// inputs would need an explicit low-limit plus overflow correction.
/// </summary>
internal sealed class ZstdFrameState
{
    private readonly byte[] _frame;
    private readonly ZstdCompressionParameters _prm;
    private readonly int _level;

    private uint[]? _fastHash;
    private uint[]? _dfastLong;
    private uint[]? _dfastSmall;
    private uint[]? _lazyHash;
    private uint[]? _lazyChain;
    private byte[]? _lazyTag;
    private ZstdOpt.OptStats? _optStats;
    private uint[]? _optHash;
    private uint[]? _optBt;
    private uint[]? _optHash3;
    private readonly ZstdEntropyState _entropy = new();
    private ZstdEntropyState? _stagedEntropy;

    /// <summary>Creates frame state over a private copy of the input.</summary>
    public ZstdFrameState(byte[] frame, int level, ZstdCompressionParameters prm)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        _frame = frame;
        _level = level;
        _prm = prm;
    }

    /// <summary>Compression level (1..22).</summary>
    public int Level => _level;

    /// <summary>Frame-level (total-size, adjusted) parameter row.</summary>
    public ZstdCompressionParameters Prm => _prm;

    /// <summary>
    /// Table-update cursor (<c>ms->nextToUpdate</c>), absolute frame offset.
    /// Persists across blocks; passed by reference into the search routines.
    /// </summary>
    public int NextToUpdate;

    /// <summary>Full frame bytes (absolute indexing).</summary>
    public ReadOnlySpan<byte> Frame => _frame;

    /// <summary>
    /// Parses <c>[blockStart, blockEnd)</c> into <paramref name="store"/>,
    /// updating <paramref name="repeatOffsets"/> per the native end-of-block
    /// rule. All strategies have a stateful port (fast, double-fast,
    /// greedy/lazy family, optimal parsers); the optimal parsers additionally
    /// persist their price statistics (<c>ms-&gt;opt</c>: first block
    /// initializes from its own bytes, later blocks scale down).
    /// </summary>
    public int FindMatches(int blockStart, int blockEnd, ZstdSequenceStore store, uint[] repeatOffsets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repeatOffsets);
        if (blockEnd < blockStart || blockStart < 0 || blockEnd > _frame.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(blockStart));
        }

        if (blockEnd == blockStart)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        return _prm.Strategy switch
        {
            ZstdStrategy.Fast => ZstdFast.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.DoubleFast => ZstdDoubleFast.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.Greedy or ZstdStrategy.Lazy or ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2 =>
                ZstdLazyEngine.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.BtOpt or ZstdStrategy.BtUltra or ZstdStrategy.BtUltra2 =>
                ZstdOpt.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            _ => throw new NotSupportedException($"No stateful port for strategy {_prm.Strategy}."),
        };
    }

    /// <summary>Persistent fast hash table (<c>1 &lt;&lt; hashLog</c>, zeroed).</summary>
    internal uint[] FastHashTable()
    {
        return _fastHash ??= new uint[1 << _prm.HashLog];
    }

    /// <summary>Persistent double-fast tables (long: <c>hashLog</c>, small: <c>chainLog</c>).</summary>
    internal (uint[] Long, uint[] Small) DoubleFastTables()
    {
        _dfastLong ??= new uint[1 << _prm.HashLog];
        _dfastSmall ??= new uint[1 << _prm.ChainLog];
        return (_dfastLong, _dfastSmall);
    }

    /// <summary>Persistent lazy hash + chain tables (hash-chain and BT searches).</summary>
    internal (uint[] Hash, uint[] Chain) LazyChainTables()
    {
        _lazyHash ??= new uint[1 << _prm.HashLog];
        _lazyChain ??= new uint[1 << _prm.ChainLog];
        return (_lazyHash, _lazyChain);
    }

    /// <summary>Persistent lazy hash + tag tables (row search).</summary>
    internal (uint[] Hash, byte[] Tag) LazyRowTables()
    {
        _lazyHash ??= new uint[1 << _prm.HashLog];
        _lazyTag ??= new byte[1 << _prm.HashLog];
        return (_lazyHash, _lazyTag);
    }

    /// <summary>
    /// Frame-persistent block entropy tables (M3:
    /// <c>prevCBlock-&gt;entropy</c>). Starts with every reuse mode at none.
    /// </summary>
    internal ZstdEntropyState Entropy => _entropy;

    /// <summary>
    /// Stages the next entropy state built for the current block (M3:
    /// <c>nextCBlock-&gt;entropy</c>). The writer confirms or drops it per
    /// the block's fate below.
    /// </summary>
    internal void StageEntropy(ZstdEntropyState next)
    {
        _stagedEntropy = next;
    }

    /// <summary>
    /// Confirms the staged entropy state after emitting a compressed block,
    /// then applies the offset-code valid→check downgrade native applies to
    /// every block (<c>ZSTD_compressBlock_internal</c> tail).
    /// </summary>
    internal void ConfirmEntropy()
    {
        if (_stagedEntropy is not null)
        {
            _entropy.CopyFrom(_stagedEntropy);
            _stagedEntropy = null;
        }

        DowngradeOffcode();
    }

    /// <summary>
    /// Drops the staged entropy state after a raw/RLE/tiny block, keeping
    /// only the offset-code valid→check downgrade native still applies.
    /// </summary>
    internal void DeclineEntropy()
    {
        _stagedEntropy = null;
        DowngradeOffcode();
    }

    private void DowngradeOffcode()
    {
        if (_entropy.OfRepeat == ZstdFseRepeat.Valid)
        {
            _entropy.OfRepeat = ZstdFseRepeat.Check;
        }
    }

    /// <summary>
    /// Persistent optimal-parser price statistics (<c>ms-&gt;opt</c> frequency
    /// half). <c>LitLengthSum == 0</c> detects the first block, exactly like
    /// <c>ZSTD_rescaleFreqs</c>.
    /// </summary>
    internal ZstdOpt.OptStats OptStats()
    {
        return _optStats ??= new ZstdOpt.OptStats();
    }

    /// <summary>
    /// Persistent optimal-parser binary-tree tables (hash, chain, and the
    /// 3-byte table when <c>minMatch</c> is 3).
    /// </summary>
    internal (uint[] Hash, uint[] Bt, uint[] Hash3) OptTables()
    {
        _optHash ??= new uint[1 << _prm.HashLog];
        _optBt ??= new uint[1 << _prm.ChainLog];
        if (_optHash3 is null)
        {
            var hashLog3 = ZstdOpt.HashLog3For(_prm);
            _optHash3 = hashLog3 > 0 ? new uint[1 << hashLog3] : [];
        }

        return (_optHash, _optBt, _optHash3);
    }
}
