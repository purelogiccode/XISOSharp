using ZARSharp.Seekable;
using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// PortPlan Step 4: seekable format (Foot + Head). Round-trip, subrange,
/// table, and error coverage that needs no oracle binary.
/// </summary>
public sealed class SeekableTests
{
    private static byte[] MakeInput(string kind, int n, int seed)
    {
        // Deterministic arithmetic mix (never HashCode: process-randomized).
        var kindIndex = kind switch
        {
            "zeros" => 0,
            "random" => 1,
            "text" => 2,
            "code" => 3,
            "binary" => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var rng = new Random(unchecked((int)(0x5332026u + (uint)kindIndex * 0x9E3779B9u + (uint)n * 31u + (uint)seed * 131u)));
        var buf = new byte[n];
        switch (kind)
        {
            case "zeros":
                break;
            case "random":
                rng.NextBytes(buf);
                break;
            case "text":
            {
                const string sample = "The quick brown fox jumps over the lazy dog. Seekable frame test. ";
                var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
                for (var i = 0; i < n; i++)
                {
                    buf[i] = ascii[(i + seed) % ascii.Length];
                }

                break;
            }

            case "code":
            {
                var tokens = new[]
                {
                    "if (x == 1) { return foo(bar); }", "for (int i = 0; i < n; i++) ",
                    "    Console.WriteLine(i);", "/* comment */", "var y = x * 2 + 1;",
                };
                var ms = new MemoryStream();
                while (ms.Length < n)
                {
                    var t = System.Text.Encoding.ASCII.GetBytes(tokens[rng.Next(tokens.Length)]);
                    ms.Write(t, 0, t.Length);
                }

                buf = ms.ToArray();
                Array.Resize(ref buf, n);
                break;
            }

            case "binary":
                for (var i = 0; i < n; i++)
                {
                    buf[i] = rng.Next(100) < 70 ? (byte)rng.Next(4) : (byte)rng.Next(256);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return buf;
    }

    private static SeekableWriter WriteAll(byte[] input, SeekableOptions options, int splits = 3)
    {
        var writer = new SeekableWriter(options);
        // Split writes prove output is independent of Write call chunking.
        var pos = 0;
        for (var i = 1; i <= splits && pos < input.Length; i++)
        {
            var take = (int)Math.Min((input.Length - pos + (splits - i)) / (splits - i + 1), input.Length - pos);
            writer.Write(new ReadOnlySpan<byte>(input, pos, take));
            pos += take;
        }

        if (pos < input.Length)
        {
            writer.Write(new ReadOnlySpan<byte>(input, pos, input.Length - pos));
        }

        return writer;
    }

    public static IEnumerable<object[]> RoundTripCases()
    {
        string[] kinds = ["zeros", "random", "text", "code", "binary"];
        int[] sizes = [0, 1, 100, 65536, 100000, 200000];
        int[] levels = [1, 3, 6, 13, 19];
        foreach (var kind in kinds)
        {
            foreach (var size in sizes)
            {
                foreach (var level in levels)
                {
                    yield return [kind, size, level, 65536, true];
                }
            }
        }

        // Checksum off + odd frame sizes + default (single-frame) policy.
        yield return ["text", 200000, 3, 100000, false];
        yield return ["binary", 131073, 6, 70000, false];
        yield return ["code", 300000, 13, 2 * 1024 * 1024, true];
        yield return ["random", 50000, 1, 2 * 1024 * 1024, false];
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_Foot(string kind, int size, int level, int frameSize, bool checksum)
    {
        var input = MakeInput(kind, size, 7);
        var options = new SeekableOptions { Level = level, FrameSize = frameSize, Checksum = checksum };
        var file = WriteAll(input, options).Finish();

        var reader = new SeekableReader(file);
        var expectedFrames = size == 0 ? 1 : (size + frameSize - 1) / frameSize;
        Assert.Equal(expectedFrames, reader.FrameCount);
        Assert.Equal(size, reader.DecompressedLength);
        Assert.Equal(input, reader.DecompressAll());

        // Table offsets are exact: frames tile both spaces without gaps.
        var table = reader.Table;
        Assert.Equal((ulong)size, table.TotalDecomp);
        for (var i = 0; i < table.FrameCount; i++)
        {
            var dStart = (long)table.FrameStartDecomp(i);
            var dSize = (long)table.FrameSizeDecomp(i);
            var want = Math.Min(frameSize, size - (int)dStart);
            Assert.Equal(want, dSize);
            if (i > 0)
            {
                Assert.Equal(table.FrameEndDecomp(i - 1), table.FrameStartDecomp(i));
                Assert.Equal(table.FrameEndComp(i - 1), table.FrameStartComp(i));
            }
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_Head(string kind, int size, int level, int frameSize, bool checksum)
    {
        var input = MakeInput(kind, size, 11);
        var options = new SeekableOptions { Level = level, FrameSize = frameSize, Checksum = checksum };
        var (data, tableBytes) = WriteAll(input, options).FinishHead();

        // Head data carries no seek table: parsing it as Foot must fail.
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(data));

        var table = SeekTable.ParseHead(tableBytes);
        var reader = new SeekableReader(data, table);
        Assert.Equal(input, reader.DecompressAll());

        // Head tables re-serialize byte-identically.
        Assert.Equal(tableBytes, table.WriteHead());
    }

    [Fact]
    public void Subranges_DecodeOnlyTouchedFrames()
    {
        var input = MakeInput("binary", 200000, 21);
        var options = new SeekableOptions { Level = 3, FrameSize = 50000 };
        var file = WriteAll(input, options).Finish();
        var reader = new SeekableReader(file);
        Assert.Equal(4, reader.FrameCount);

        var rng = new Random(0x5332026);
        for (var i = 0; i < 40; i++)
        {
            var start = rng.Next(200000);
            var len = rng.Next(200000 - start + 1);
            Assert.Equal(input.AsSpan(start, len).ToArray(), reader.DecompressRange(start, len));
        }

        // Boundary-aligned and degenerate ranges.
        Assert.Equal(input.AsSpan(50000, 50000).ToArray(), reader.DecompressRange(50000, 50000));
        Assert.Equal(input.AsSpan(49999, 2).ToArray(), reader.DecompressRange(49999, 2));
        Assert.Equal([input[123456]], reader.DecompressRange(123456, 1));
        Assert.Empty(reader.DecompressRange(99999, 0));
        Assert.Empty(reader.DecompressRange(200000, 0));

        // Frame windows.
        Assert.Equal(input.AsSpan(0, 100000).ToArray(), reader.DecompressFrames(0, 1));
        Assert.Equal(input.AsSpan(150000, 50000).ToArray(), reader.DecompressFrames(3, 3));
        Assert.Equal(input, reader.DecompressFrames(0, 3));
    }

    [Fact]
    public void ExactFrameBoundary_LogsNoTrailingEmptyFrame()
    {
        var input = MakeInput("text", 100000, 5);
        var options = new SeekableOptions { Level = 3, FrameSize = 50000 };
        var reader = new SeekableReader(WriteAll(input, options).Finish());
        Assert.Equal(2, reader.FrameCount);
        Assert.Equal(input, reader.DecompressAll());
    }

    [Fact]
    public void SplitWrites_AreByteIdentical()
    {
        var input = MakeInput("code", 150000, 9);
        var options = new SeekableOptions { Level = 6, FrameSize = 40000 };
        var one = new SeekableWriter(options);
        one.Write(input);
        var many = WriteAll(input, options, splits: 17);
        Assert.Equal(one.Finish(), many.Finish());

        var cOptions = new SeekableOptions { Level = 6, FrameSize = 8192, Policy = SeekableFrameSizePolicy.Compressed };
        var oneC = new SeekableWriter(cOptions);
        oneC.Write(input);
        var manyC = WriteAll(input, cOptions, splits: 17);
        Assert.Equal(oneC.Finish(), manyC.Finish());
    }

    [Fact]
    public void CompressedPolicy_RoundTrips()
    {
        var input = MakeInput("binary", 200000, 13);
        var options = new SeekableOptions { Level = 3, FrameSize = 16384, Policy = SeekableFrameSizePolicy.Compressed };
        var file = WriteAll(input, options).Finish();
        var reader = new SeekableReader(file);
        Assert.True(reader.FrameCount > 1);
        Assert.Equal(input, reader.DecompressAll());

        var rng = new Random(0x5332026);
        for (var i = 0; i < 20; i++)
        {
            var start = rng.Next(200000);
            var len = rng.Next(200000 - start + 1);
            Assert.Equal(input.AsSpan(start, len).ToArray(), reader.DecompressRange(start, len));
        }
    }

    [Fact]
    public void SeekTable_SerializeParseCycle()
    {
        var input = MakeInput("text", 120000, 3);
        var file = WriteAll(input, new SeekableOptions { Level = 3, FrameSize = 50000 }).Finish();
        var table = SeekTable.ParseFoot(file);
        Assert.Equal(3, table.FrameCount);
        Assert.Equal(table.WriteFoot(), file[^table.WriteFoot().Length..]);

        // Index math matches brute force on every boundary.
        for (var i = 0; i < 3; i++)
        {
            var start = (int)table.FrameStartDecomp(i);
            var end = (int)table.FrameEndDecomp(i);
            Assert.Equal(i, table.FrameIndexAtDecomp((ulong)start));
            Assert.Equal(Math.Min(i + 1, 2), table.FrameIndexAtDecomp((ulong)end));
            var cstart = (int)table.FrameStartComp(i);
            var cend = (int)table.FrameEndComp(i);
            Assert.Equal(i, table.FrameIndexAtComp((ulong)cstart));
            Assert.Equal(Math.Min(i + 1, 2), table.FrameIndexAtComp((ulong)cend));
            Assert.Equal((ulong)(cend - cstart), table.FrameSizeComp(i));
        }

        Assert.Equal((ulong)120000, table.TotalDecomp);
        Assert.Equal((ulong)50000, table.MaxFrameSizeDecomp());
    }

    [Fact]
    public void SeekTable_LegacyChecksumsAreIgnored()
    {
        // Hand-built Foot table with Checksum_Flag set: 12-byte entries whose
        // trailing checksums must be skipped, not interpreted.
        var buf = new List<byte>();
        buf.AddRange(new byte[] { 0x5E, 0x2A, 0x4D, 0x18 });
        buf.AddRange(BitConverter.GetBytes(33u)); // 2 x 12 entries + 9 integrity
        buf.AddRange(BitConverter.GetBytes(100u));
        buf.AddRange(BitConverter.GetBytes(200u));
        buf.AddRange(BitConverter.GetBytes(0xDEADBEEFu)); // ignored
        buf.AddRange(BitConverter.GetBytes(300u));
        buf.AddRange(BitConverter.GetBytes(400u));
        buf.AddRange(BitConverter.GetBytes(0x12345678u)); // ignored
        buf.AddRange(BitConverter.GetBytes(2u));
        buf.Add(0x80); // Checksum_Flag, reserved bits clear
        buf.AddRange(new byte[] { 0xB1, 0xEA, 0x92, 0x8F });
        var file = new byte[400];
        var table = SeekTable.ParseFoot([.. file, .. buf]);
        Assert.Equal(2, table.FrameCount);
        Assert.Equal(100u, table.FrameStartComp(1));
        Assert.Equal(200u, table.FrameStartDecomp(1));
        Assert.Equal(400u, table.FrameEndComp(1));
        Assert.Equal(600u, table.FrameEndDecomp(1));
        Assert.Equal(1, table.FrameIndexAtDecomp(599));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void SeekTable_RejectsTruncated(int length)
    {
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(new byte[length]));
        Assert.Throws<ZstdException>(() => SeekTable.ParseHead(new byte[length]));
    }

    [Fact]
    public void SeekTable_RejectsCorrupt()
    {
        var input = MakeInput("text", 1000, 3);
        var file = WriteAll(input, new SeekableOptions { Level = 3, FrameSize = 500 }).Finish();

        // Table is 8 + 2*8 + 9 = 33 bytes; header starts at file.Length - 33.
        // Bad skippable magic.
        var bad = (byte[])file.Clone();
        bad[^33] ^= 0xFF;
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(bad));

        // Bad seekable magic.
        bad = (byte[])file.Clone();
        bad[^1] ^= 0xFF;
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(bad));

        // Reserved descriptor bits set.
        bad = (byte[])file.Clone();
        bad[^5] |= 0x04;
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(bad));

        // Frame-size field mismatch.
        bad = (byte[])file.Clone();
        bad[^29] ^= 0xFF;
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(bad));

        // Not a seek table at all.
        Assert.Throws<ZstdException>(() => SeekTable.ParseFoot(new byte[64]));
        Assert.Throws<ZstdException>(() => new SeekableReader(new byte[64]));
    }

    [Fact]
    public void Reader_RejectsBadRangesAndFrames()
    {
        var input = MakeInput("text", 1000, 3);
        var file = WriteAll(input, new SeekableOptions { Level = 3, FrameSize = 500 }).Finish();
        var reader = new SeekableReader(file);
        Assert.Throws<ZstdException>(() => reader.DecompressRange(0, 1001));
        Assert.Throws<ZstdException>(() => reader.DecompressRange(1000, 1));
        Assert.Throws<ZstdException>(() => reader.DecompressFrames(0, 2));
        Assert.Throws<ZstdException>(() => reader.DecompressFrames(2, 2));
        Assert.Throws<ZstdException>(() => reader.DecompressFrames(1, 0));

        // Corrupt frame payload surfaces as a decode error.
        var corrupt = (byte[])file.Clone();
        corrupt[20] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => new SeekableReader(corrupt).DecompressAll());
    }

    [Fact]
    public void Writer_RejectsBadOptionsAndUseAfterFinish()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeekableWriter(new SeekableOptions { Level = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeekableWriter(new SeekableOptions { Level = 23 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeekableWriter(new SeekableOptions { FrameSize = 0 }));
        var writer = new SeekableWriter();
        writer.Write([1, 2, 3]);
        writer.Finish();
        Assert.Throws<ObjectDisposedException>(() => writer.Write([4]));
        Assert.Throws<ObjectDisposedException>(() => writer.Finish());
    }

    [Fact]
    public void FrameChecksum_IsVerified()
    {
        var input = MakeInput("text", 100000, 3);
        var file = WriteAll(input, new SeekableOptions { Level = 3, FrameSize = 50000 }).Finish();
        var reader = new SeekableReader(file);

        // Flip a byte in the last frame's 4-byte content checksum trailer.
        var endFirst = (int)reader.Table.FrameEndComp(1);
        var corrupt = (byte[])file.Clone();
        corrupt[endFirst - 1] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => new SeekableReader(corrupt).DecompressAll());
    }
}
