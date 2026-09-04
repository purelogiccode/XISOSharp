namespace ZARSharp.Zstd;

/// <summary>
/// Level-6 entry point over <see cref="ZstdLazyEngine"/> (kept as a named
/// alias for the Step 2 port: <c>ZSTD_compressBlock_lazy_generic</c> depth 1,
/// depth 2 for inputs ≤ 16 KiB, row hash when the adjusted windowLog &gt; 14).
/// </summary>
internal static class ZstdLazyLevel6
{
    /// <summary>Fresh-context row hash salt (see <see cref="ZstdLazyEngine"/>).</summary>
    internal static ulong FreshHashSalt => ZstdLazyEngine.FreshHashSalt;

    /// <summary><c>ZSTD_hashPtrSalted</c> (see <see cref="ZstdLazyEngine"/>).</summary>
    internal static uint HashSalted(ReadOnlySpan<byte> src, int pos, int hBits, int minMatch, ulong salt)
    {
        return ZstdLazyEngine.HashSalted(src, pos, hBits, minMatch, salt);
    }

    /// <summary>
    /// Parses <paramref name="source"/> exactly like native level 6.
    /// </summary>
    internal static int FindMatches(
        ReadOnlySpan<byte> source, ZstdSequenceStore store, uint[] repeatOffsets)
    {
        return ZstdLazyEngine.FindMatches(source, store, repeatOffsets, 6);
    }
}
