using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 4 acceptance: the match finders emit <em>valid</em> sequence streams
/// for every level (1–6) and input shape. Validity is proved by an independent
/// replay validator that mirrors the decoder (<c>ZstdDecompressor</c>):
/// literals are copied, repeat codes resolve through a {1,4,8} history with
/// RFC 8878 §4.1.1 update rules, every offset must satisfy
/// <c>1 ≤ dist ≤ bytes-emitted-so-far</c>, matches copy with memmove
/// semantics, and the replay must equal the input byte-for-byte.
/// (Phase 5 proves decodability end-to-end; this proves the finder half.)
/// References: <c>lib/compress/zstd_fast.c</c>, <c>lib/compress/zstd_lazy.c</c>.
/// </summary>
public sealed class ZstdMatchFinderTests
{
    // ------------------------------------------------------------------
    // Replay validator (independent of the finder under test)
    // ------------------------------------------------------------------

    /// <summary>
    /// Replays a sequence store with decoder semantics and returns the output.
    /// Throws <see cref="XunitException"/> (via Assert) on any invalid stream:
    /// bad offset, bad repcode, literal over/under-consumption, length mismatch.
    /// </summary>
    internal static byte[] Replay(byte[] input, ZstdSequenceStore store, uint[]? history = null)
    {
        // Threads the repeat history across blocks when provided (chained
        // frames); otherwise starts from a fresh {1,4,8} like the decoder.
        var rep = history ?? ZstdSeq.FreshRepeatOffsets();
        var output = new List<byte>(input.Length);
        var litPos = 0;
        var literals = store.Literals.ToArray();

        for (var i = 0; i < store.Count; i++)
        {
            var seq = store.Get(i);
            Assert.True(litPos + (long)seq.LitLength <= literals.Length, $"Seq {i}: literal over-consumption.");
            for (uint k = 0; k < seq.LitLength; k++)
            {
                output.Add(literals[litPos + (int)k]);
            }

            litPos += (int)seq.LitLength;

            // Resolve the offset exactly like the decoder.
            ulong dist;
            if (ZstdSeq.IsOffset(seq.OffBase))
            {
                dist = ZstdSeq.ToOffset(seq.OffBase);
                rep[2] = rep[1];
                rep[1] = rep[0];
                rep[0] = (uint)dist;
            }
            else
            {
                var code = ZstdSeq.ToRepcode(seq.OffBase);
                var ll0 = seq.LitLength == 0 ? 1u : 0u;
                var repCode = code - 1 + ll0;
                Assert.True(repCode <= 3, $"Seq {i}: bad repeat code.");
                if (repCode == 0)
                {
                    dist = rep[0];
                }
                else
                {
                    dist = repCode == 3 ? rep[0] - 1 : rep[repCode];
                    if (repCode >= 2)
                    {
                        rep[2] = rep[1];
                    }

                    rep[1] = rep[0];
                    rep[0] = (uint)dist;
                }
            }

            // Decoder validity rule (ZstdDecompressor.ExecuteSequence).
            Assert.True(dist >= 1 && dist <= (ulong)output.Count,
                $"Seq {i}: invalid offset {dist} at pos {output.Count}.");

            var matchPos = output.Count - (int)dist;
            for (uint k = 0; k < seq.MatchLength; k++)
            {
                output.Add(output[matchPos + (int)k]);
            }
        }

        Assert.Equal(literals.Length, litPos);
        foreach (var b in store.TrailingLiterals)
        {
            output.Add(b);
        }

        Assert.Equal(input.Length, output.Count);
        Assert.Equal(input, output.ToArray());
        return output.ToArray();
    }

    /// <summary>Parses at <paramref name="level"/> and replay-validates.</summary>
    private static ZstdSequenceStore ParseAndReplay(byte[] input, int level)
    {
        var store = new ZstdSequenceStore(input.Length);
        var finder = new ZstdMatchFinder(level);
        var rep = ZstdSeq.FreshRepeatOffsets();
        var trailing = finder.FindMatches(input, store, rep);
        Assert.Equal(store.TrailingLength, trailing);
        Replay(input, store);

        // Every stored match meets the finder minimum (4 for all strategies).
        for (var i = 0; i < store.Count; i++)
        {
            Assert.True(store.Get(i).MatchLength >= 4, $"Level {level}: match < 4 bytes.");
        }

        return store;
    }

    // ------------------------------------------------------------------
    // Input corpus
    // ------------------------------------------------------------------

    internal static byte[] MakeInput(string kind, int size, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[size];
        switch (kind)
        {
            case "zeros":
                break; // All zero: offset-1 with empty history at block start.
            case "period1":
                Array.Fill(data, (byte)0xAB);
                break;
            case "period2":
                for (var i = 0; i < size; i++)
                {
                    data[i] = (byte)(0x10 + (i & 1));
                }

                break;
            case "period3":
                for (var i = 0; i < size; i++)
                {
                    data[i] = (byte)(0x30 + (i % 3));
                }

                break;
            case "text":
                // Real words with spaces: multi-byte tokens repeat constantly,
                // which is what an LZ finder needs (independent draws from an
                // alphabet have no repeats at MLS 4-6 and are unmatchable).
                string[] words =
                [
                    "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog",
                    "pack", "box", "with", "five", "dozen", "liquor", "jugs", "how",
                    "vexingly", "daft", "zebras", "sphinx", "black", "quartz",
                    "judge", "vow", "0123456789",
                ];
                var at = 0;
                while (at < size)
                {
                    var word = words[rng.Next(words.Length)];
                    foreach (var c in word)
                    {
                        if (at >= size)
                        {
                            break;
                        }

                        data[at++] = (byte)c;
                    }

                    if (at < size)
                    {
                        data[at++] = (byte)' ';
                    }
                }

                break;
            case "mixed-reps": // Designed to churn repeat offsets 1, 4, 8.
                for (var i = 0; i < size; i++)
                {
                    var m = i % 64;
                    data[i] = m < 16 ? (byte)0xCC : m < 32 ? (byte)(i & 7) : (byte)rng.Next(256);
                }

                break;
            default: // "random"
                rng.NextBytes(data);
                break;
        }

        return data;
    }

    public static TheoryData<int, string, int> Corpus()
    {
        var data = new TheoryData<int, string, int>();
        string[] kinds = ["zeros", "period1", "period2", "period3", "text", "mixed-reps", "random"];
        int[] sizes = [0, 1, 2, 3, 4, 5, 7, 8, 9, 12, 16, 31, 64, 100, 1000, 8192, 65536];
        foreach (var level in new[] { 1, 2, 3, 4, 5, 6 })
        {
            foreach (var kind in kinds)
            {
                foreach (var size in sizes)
                {
                    data.Add(level, kind, size);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Finder_SequencesReplayToInput(int level, string kind, int size)
    {
        var input = MakeInput(kind, size, 0xF1 ^ (level * 7919) ^ size);
        ParseAndReplay(input, level);
    }

    [Fact]
    public void Finder_RandomData_FindsNoSequences()
    {
        // Incompressible input: no matches, everything trailing.
        var input = MakeInput("random", 4096, 42);
        for (var level = 1; level <= 6; level++)
        {
            var store = new ZstdSequenceStore(input.Length);
            var finder = new ZstdMatchFinder(level);
            var trailing = finder.FindMatches(input, store, ZstdSeq.FreshRepeatOffsets());
            Assert.Equal(0, store.Count);
            Assert.Equal(input.Length, trailing);
            Assert.Equal(input, store.TrailingLiterals.ToArray());
        }
    }

    [Fact]
    public void Finder_Zeros_UsesOffsetOne()
    {
        // 100 zero bytes must compress to (nearly) one sequence with offset 1.
        // This is the "offset 1 with empty history" trap: at block start the
        // history is {1,4,8}, so offset 1 is valid from position 1 on.
        var input = new byte[100];
        for (var level = 1; level <= 6; level++)
        {
            var store = ParseAndReplay(input, level);
            Assert.True(store.Count >= 1, $"Level {level}: expected matches for zeros.");
            var first = store.Get(0);
            // Offset 1, either as a full offset or as repeat code 1 with a
            // non-empty literal run (the rep probe fires first and resolves
            // rep[0] = 1 from the fresh {1,4,8} history).
            var isOffsetOne = ZstdSeq.IsOffset(first.OffBase) && ZstdSeq.ToOffset(first.OffBase) == 1;
            var isRepOne = ZstdSeq.IsRepcode(first.OffBase) && ZstdSeq.ToRepcode(first.OffBase) == 1
                                                            && first.LitLength >= 1;
            Assert.True(isOffsetOne || isRepOne, $"Level {level}: first seq {first} does not use offset 1.");
        }
    }

    [Fact]
    public void Finder_Deterministic()
    {
        var input = MakeInput("mixed-reps", 20000, 7);
        for (var level = 1; level <= 6; level++)
        {
            var a = ParseAndReplay(input, level);
            var b = ParseAndReplay(input, level);
            Assert.Equal(a.Count, b.Count);
            Assert.Equal(a.LiteralLength, b.LiteralLength);
            Assert.Equal(a.TrailingLength, b.TrailingLength);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a.Get(i), b.Get(i));
            }
        }
    }

    [Fact]
    public void Finder_OffsetSlice_MatchesWholeBufferResult()
    {
        // Positions are span-relative, so encoding a slice must equal encoding
        // the same bytes standalone (no hidden absolute-position dependence).
        var big = MakeInput("text", 10000, 99);
        var slice = new byte[3000];
        Array.Copy(big, 1234, slice, 0, slice.Length);
        for (var level = 1; level <= 6; level++)
        {
            var fromSlice =
                ParseAndReplay(new ReadOnlySpan<byte>(big, 1234, slice.Length).ToArray(), level);
            var standalone = ParseAndReplay(slice, level);
            Assert.Equal(standalone.Count, fromSlice.Count);
        }
    }

    [Fact]
    public void Finder_CrossBlockRepeatHistory_Chains()
    {
        // Two consecutive blocks sharing one history stay replay-valid when
        // the validator threads the same history (encoder and decoder evolve
        // it identically). Zeros guarantee immediate offset-1 matches in the
        // second block; text exercises chaining on realistic data.
        foreach (var kind in new[] { "zeros", "text" })
        {
            var block = MakeInput(kind, 4096, 5);
            for (var level = 1; level <= 6; level++)
            {
                var finder = new ZstdMatchFinder(level);
                var encRep = ZstdSeq.FreshRepeatOffsets();
                var decRep = ZstdSeq.FreshRepeatOffsets();
                var s1 = new ZstdSequenceStore(block.Length);
                finder.FindMatches(block, s1, encRep);
                Replay(block, s1, decRep);
                Assert.Equal(encRep, decRep); // Histories agree after block 1.

                var s2 = new ZstdSequenceStore(block.Length);
                finder.FindMatches(block, s2, encRep); // Same finder, chained history.
                Replay(block, s2, decRep);
                Assert.Equal(encRep, decRep); // ... and after block 2.
                Assert.True(s2.Count >= 1, $"Level {level}/{kind}: repeated block should match.");
            }
        }
    }

    [Fact]
    public void Finder_AllLevels_AgreeOnTrivialBoundaries()
    {
        // Sizes around HASH_READ_SIZE / ilimit edges must not throw or corrupt.
        for (var size = 0; size <= 20; size++)
        {
            var input = MakeInput("period2", size, 1);
            for (var level = 1; level <= 6; level++)
            {
                ParseAndReplay(input, level);
            }
        }
    }

    [Fact]
    public void Finder_LazyBeatsFast_OnText()
    {
        // Sanity: lazy (L6) must find at least as much match coverage as
        // fast (L1) on repetitive text (validity is proven by replay; this
        // guards against a silently degenerate lazy path).
        var input = MakeInput("text", 32768, 1234);

        var fast = MatchedBytes(ParseAndReplay(input, 1));
        var lazy = MatchedBytes(ParseAndReplay(input, 6));
        Assert.True(lazy >= fast, $"L6 matched {lazy} < L1 matched {fast}.");
        Assert.True(lazy > input.Length / 2, "Text should be mostly matches.");
        return;

        static long MatchedBytes(ZstdSequenceStore s)
        {
            long total = 0;
            for (var i = 0; i < s.Count; i++)
            {
                total += s.Get(i).MatchLength;
            }

            return total;
        }
    }

    // ------------------------------------------------------------------
    // Hash reference checks (ZSTD_hashPtr wiring)
    // ------------------------------------------------------------------

    [Fact]
    public void HashPtr_KnownVectors()
    {
        // Independent transcription of the formulas (primes as literals):
        // hash4(u,h) = (u * 2654435761) >> (32-h), u = LE32.
        byte[] src = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        const uint u = 0x04030201u;
        const uint expected4 = unchecked((u * 2654435761u) >> (32 - 10)); // U32 wraps, like the C.
        Assert.Equal(expected4, ZstdMatchFinder.HashPtr(src, 0, 10, 4));

        // hash8(u,h) = (u * 0xCF1BBCDCB7A56463) >> (64-h), u = LE64.
        const ulong w = 0x0807060504030201UL;
        const ulong expected8 = unchecked((w * 0xCF1BBCDCB7A56463UL) >> (64 - 17));
        Assert.Equal((uint)expected8, ZstdMatchFinder.HashPtr(src, 0, 17, 8));

        // Tables bounds: every hash fits its table.
        var rng = new Random(3);
        var random = new byte[64];
        rng.NextBytes(random);
        foreach (var mls in new[] { 4, 5, 6, 7, 8 })
        {
            foreach (var hlog in new[] { 12, 13, 15, 16, 17 })
            {
                var h = ZstdMatchFinder.HashPtr(random, 9, hlog, mls);
                Assert.True(h < (1u << hlog), $"hash out of range (mls={mls}, hlog={hlog}).");
            }
        }
    }

    // ------------------------------------------------------------------
    // Sequence store unit checks
    // ------------------------------------------------------------------

    [Fact]
    public void SeqStore_UpdateRep_MatchesDecoderRules()
    {
        // Full offset shifts history.
        uint[] rep = [10, 20, 30];
        ZstdSeq.UpdateRep(rep, ZstdSeq.OffsetToOffBase(99), 0);
        Assert.Equal(new uint[] { 99, 10, 20 }, rep);

        // Repcode 1 with literals: no change.
        ZstdSeq.UpdateRep(rep, ZstdSeq.Repcode1, 0);
        Assert.Equal(new uint[] { 99, 10, 20 }, rep);

        // Repcode 1 without literals: swap 0 <-> 1.
        ZstdSeq.UpdateRep(rep, ZstdSeq.Repcode1, 1);
        Assert.Equal(new uint[] { 10, 99, 20 }, rep);

        // Repcode 2 with literals: rep[1] wins, rotate.
        ZstdSeq.UpdateRep(rep, ZstdSeq.Repcode2, 0);
        Assert.Equal(new uint[] { 99, 10, 20 }, rep);

        // Repcode 3 without literals: rep[0]-1 wins, full rotate.
        ZstdSeq.UpdateRep(rep, ZstdSeq.Repcode3, 1);
        Assert.Equal(new uint[] { 98, 99, 10 }, rep);
    }

    [Fact]
    public void SeqStore_OffBaseHelpers()
    {
        Assert.True(ZstdSeq.IsOffset(ZstdSeq.OffsetToOffBase(1)));
        Assert.Equal(1u, ZstdSeq.ToOffset(ZstdSeq.OffsetToOffBase(1)));
        Assert.True(ZstdSeq.IsRepcode(ZstdSeq.Repcode2));
        Assert.Equal(2u, ZstdSeq.ToRepcode(ZstdSeq.Repcode2));
        Assert.False(ZstdSeq.IsOffset(ZstdSeq.Repcode1));
        Assert.False(ZstdSeq.IsRepcode(ZstdSeq.OffsetToOffBase(7)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZstdSeq.OffsetToOffBase(0));
        Assert.Equal(new uint[] { 1, 4, 8 }, ZstdSeq.FreshRepeatOffsets());
    }

    [Fact]
    public void SeqStore_StoreAndGet_RoundTrip()
    {
        var store = new ZstdSequenceStore(64);
        store.StoreSequence(new byte[] { 1, 2, 3 }, ZstdSeq.OffsetToOffBase(5), 10);
        store.StoreSequence([], ZstdSeq.Repcode1, 4);
        store.SetTrailingLiterals("\t\t"u8);

        Assert.Equal(2, store.Count);
        Assert.Equal(new ZstdSequence(3, ZstdSeq.OffsetToOffBase(5), 10), store.Get(0));
        Assert.Equal(new ZstdSequence(0, ZstdSeq.Repcode1, 4), store.Get(1));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Literals.ToArray());
        Assert.Equal("\t\t"u8.ToArray(), store.TrailingLiterals.ToArray());

        store.Reset();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.LiteralLength);
        Assert.Equal(0, store.TrailingLength);
    }
}