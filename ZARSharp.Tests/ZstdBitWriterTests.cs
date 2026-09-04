using ZARSharp.Zstd;

namespace ZARSharp.Tests;

/// <summary>
/// Phase 1 acceptance: write → read symmetry with <c>ZstdBitReader</c> for
/// 1..31-bit values, unaligned sequences, and multi-flush streams.
/// Mirrors <c>lib/common/bitstream.h</c> semantics (LIFO for CStream).
/// </summary>
public sealed class ZstdBitWriterTests
{
    [Fact]
    public void CStream_LifoSymmetry_SingleValues()
    {
        // Write values forward, read back in reverse (LIFO stack).
        var cases = new (ulong Value, int Bits)[]
        {
            (0, 1), (1, 1), (5, 3), (0xAB, 8), (0x1234, 13), (0x7FFFFFFF, 31), (0, 5), (1, 24),
        };
        byte[] buf = new byte[64];
        var writer = new CStreamWriter(buf, 0, buf.Length);
        foreach (var (v, n) in cases)
        {
            writer.AddBits(v, n);
            writer.FlushBits();
        }

        int size = writer.Close();
        Assert.True(size > 0 && size <= buf.Length);

        var reader = BackwardBitReader.ForSequenceStream(buf, 0, size);
        for (int i = cases.Length - 1; i >= 0; i--)
        {
            Assert.Equal((uint)cases[i].Value, reader.ReadBits(cases[i].Bits));
        }

        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void CStream_RandomizedLifoRoundTrip()
    {
        var rnd = new Random(0xC0DEC);
        for (int trial = 0; trial < 50; trial++)
        {
            int count = rnd.Next(1, 12);
            var seq = new (uint Value, int Bits)[count];
            for (int i = 0; i < count; i++)
            {
                int n = rnd.Next(1, 24); // keep bitPos < 64 with per-add flush
                uint max = n == 32 ? uint.MaxValue : ((1u << n) - 1);
                seq[i] = ((uint)rnd.NextInt64(0, (long)max + 1), n);
            }

            byte[] buf = new byte[256];
            var writer = new CStreamWriter(buf, 0, buf.Length);
            foreach (var (v, n) in seq)
            {
                writer.AddBits(v, n);
                writer.FlushBits();
            }

            int size = writer.Close();
            var reader = BackwardBitReader.ForSequenceStream(buf, 0, size);
            for (int i = count - 1; i >= 0; i--)
            {
                Assert.Equal(seq[i].Value, reader.ReadBits(seq[i].Bits));
            }

            Assert.True(reader.IsAtEnd);
        }
    }

    [Fact]
    public void CStream_UnalignedMultiFlush_BitExact()
    {
        byte[] buf = new byte[16];
        var writer = new CStreamWriter(buf, 0, buf.Length);
        // 3 + 10 + 5 bits without intermediate flush (18 bits < 64, no overflow).
        writer.AddBits(0b101, 3);
        writer.AddBits(0b1111000011, 10);
        writer.AddBits(0b10101, 5);
        writer.FlushBits();
        int size = writer.Close();

        var reader = BackwardBitReader.ForSequenceStream(buf, 0, size);
        Assert.Equal(0b10101u, reader.ReadBits(5));
        Assert.Equal(0b1111000011u, reader.ReadBits(10));
        Assert.Equal(0b101u, reader.ReadBits(3));
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void CStream_OverflowThrows_NeverTruncates()
    {
        byte[] tiny = new byte[9]; // capacity <= 8 rejected at init
        Assert.Throws<ZstdException>(() => new CStreamWriter(tiny, 0, 8));

        byte[] buf = new byte[16];
        var writer = new CStreamWriter(buf, 0, buf.Length);
        // Fill container without flushing: 31 + 31 = 62 bits OK, +2 overflows.
        writer.AddBits(1, 31);
        writer.AddBits(1, 31);
        Assert.Throws<ZstdException>(() => writer.AddBits(3, 2));
        Assert.Throws<ZstdException>(() => writer.AddBits(0, 32));
    }

    [Fact]
    public void CStream_CloseWritesEndMark()
    {
        byte[] buf = new byte[16];
        var writer = new CStreamWriter(buf, 0, buf.Length);
        writer.AddBits(0x55, 8);
        int size = writer.Close();
        Assert.True(size >= 2); // payload + end-mark byte
        Assert.NotEqual(0, buf[size - 1]); // end mark present (highest set bit)
        var reader = BackwardBitReader.ForSequenceStream(buf, 0, size);
        Assert.Equal(0x55u, reader.ReadBits(8));
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void ForwardWriter_Symmetry_WithForwardReader()
    {
        var rnd = new Random(1234);
        var seq = new (uint Value, int Bits)[20];
        for (int i = 0; i < seq.Length; i++)
        {
            int n = rnd.Next(1, 16);
            seq[i] = ((uint)rnd.Next(0, 1 << n), n);
        }

        byte[] buf = new byte[64];
        var writer = new ForwardBitWriter(buf, 0, buf.Length);
        foreach (var (v, n) in seq)
        {
            writer.AddBits(v, n);
        }

        int size = writer.Flush();

        var reader = new ForwardBitReader(buf, 0, size);
        foreach (var (v, n) in seq)
        {
            Assert.Equal(v, reader.ReadBits(n));
        }
    }

    [Fact]
    public void ForwardWriter_ZeroPadsLastByte()
    {
        byte[] buf = new byte[8];
        var writer = new ForwardBitWriter(buf, 0, buf.Length);
        writer.AddBits(0b11, 2);
        int size = writer.Flush();
        Assert.Equal(1, size);
        Assert.Equal(0b11, buf[0] & 0b11);
        Assert.Equal(0, buf[0] >> 2); // zero padding
    }

    [Fact]
    public void ForwardWriter_OverflowThrows()
    {
        byte[] buf = new byte[2];
        var writer = new ForwardBitWriter(buf, 0, buf.Length);
        writer.AddBits(0xFF, 8);
        writer.AddBits(0xFF, 8);
        Assert.Throws<ZstdException>(() => writer.AddBits(1, 1));
    }
}
