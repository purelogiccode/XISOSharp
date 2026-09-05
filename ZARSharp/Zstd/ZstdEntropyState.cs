namespace ZARSharp.Zstd;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>
/// FSE table reuse mode (<c>FSE_repeat_none/check/valid</c>,
/// <c>lib/common/fse.h</c>).
/// </summary>
internal enum ZstdFseRepeat
{
    /// <summary>No previous table (<c>FSE_repeat_none</c>).</summary>
    None = 0,

    /// <summary>
    /// Previous table usable after re-validation
    /// (<c>FSE_repeat_check</c>).
    /// </summary>
    Check = 1,

    /// <summary>Previous table trusted (<c>FSE_repeat_valid</c>).</summary>
    Valid = 2,
}

/// <summary>
/// Huffman table reuse mode (<c>HUF_repeat_none/check/valid</c>,
/// <c>lib/common/huf.h</c>).
/// </summary>
internal enum ZstdHufRepeat
{
    /// <summary>No previous table (<c>HUF_repeat_none</c>).</summary>
    None = 0,

    /// <summary>
    /// Previous table usable after re-validation
    /// (<c>HUF_repeat_check</c>).
    /// </summary>
    Check = 1,

    /// <summary>Previous table trusted (<c>HUF_repeat_valid</c>).</summary>
    Valid = 2,
}

/// <summary>
/// Frame-persistent block entropy tables for multi-block frames: the
/// previous block's Huffman table plus the three FSE tables with their
/// reuse modes — the C# half of native <c>ZSTD_compressedBlockState_t</c>
/// (<c>prevCBlock-&gt;entropy</c> / <c>nextCBlock-&gt;entropy</c>,
/// <c>lib/compress/zstd_compress.c</c>). The first block starts with every
/// mode at none (no previous tables); each emitted compressed block selects
/// per alphabet between rebuilding and repeating
/// (<c>ZSTD_selectEncodingType</c> for sequences,
/// <c>HUF_compress_internal</c> for literals). Like native, only emitted
/// compressed blocks confirm the staged next state into this one
/// (<c>ZSTD_blockState_confirmRepcodesAndEntropyTables</c> swaps on
/// <c>cSize &gt; 1</c> only); raw/RLE/declined blocks leave it untouched
/// except for the offset-code valid→check downgrade the C code applies to
/// every block. Tables are immutable once built, so repeat modes share the
/// table objects across blocks.
/// </summary>
internal sealed class ZstdEntropyState
{
    /// <summary>Huffman reuse mode.</summary>
    public ZstdHufRepeat HufRepeat;

    /// <summary>Previous Huffman table (null until the first compressed table).</summary>
    public HuffmanCTable? HufTable;

    /// <summary>Literal-length reuse mode.</summary>
    public ZstdFseRepeat LlRepeat;

    /// <summary>Offset-code reuse mode.</summary>
    public ZstdFseRepeat OfRepeat;

    /// <summary>Match-length reuse mode.</summary>
    public ZstdFseRepeat MlRepeat;

    /// <summary>Previous literal-length table.</summary>
    public FseCTable? LlTable;

    /// <summary>Previous offset-code table.</summary>
    public FseCTable? OfTable;

    /// <summary>Previous match-length table.</summary>
    public FseCTable? MlTable;

    /// <summary>
    /// Copies every mode and table reference from <paramref name="other"/>
    /// (native <c>ZSTD_memcpy(nextHuf, prevHuf)</c> /
    /// <c>memcpy(&amp;nextEntropy-&gt;fse, &amp;prevEntropy-&gt;fse)</c>).
    /// </summary>
    public void CopyFrom(ZstdEntropyState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        HufRepeat = other.HufRepeat;
        HufTable = other.HufTable;
        LlRepeat = other.LlRepeat;
        OfRepeat = other.OfRepeat;
        MlRepeat = other.MlRepeat;
        LlTable = other.LlTable;
        OfTable = other.OfTable;
        MlTable = other.MlTable;
    }
}
