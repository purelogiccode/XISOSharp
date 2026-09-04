using System.Buffers.Binary;

namespace ZARSharp.Zstd;

/// <summary>
/// XXH64 hash (seed 0) for zstd content checksums (RFC 8878 Section 3.1.1).
/// Only the low 4 bytes (little-endian) are stored in frames.
/// </summary>
internal static class ZstdXxh64
{
    private const ulong P1 = 0x9E3779B185EBCA87UL;
    private const ulong P2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong P3 = 0x165667B19E3779F9UL;
    private const ulong P4 = 0x85EBCA77C2B2AE63UL;
    private const ulong P5 = 0x27D4EB2F165667C5UL;

    private static ulong Rotl(ulong v, int r) => (v << r) | (v >> (64 - r));

    private static ulong Round(ulong acc, ulong input)
    {
        acc += input * P2;
        acc = Rotl(acc, 31);
        acc *= P1;
        return acc;
    }

    private static ulong MergeRound(ulong hash, ulong v)
    {
        v = Round(0, v);
        hash ^= v;
        hash = hash * P1 + P4;
        return hash;
    }

    /// <summary>Computes XXH64 over <paramref name="data"/> with seed 0.</summary>
    public static ulong Hash64(byte[] data, int offset, int length)
    {
        int p = offset;
        int end = offset + length;
        ulong hash;
        if (length >= 32)
        {
            ulong v1 = unchecked(P1 + P2);
            ulong v2 = P2;
            ulong v3 = 0;
            ulong v4 = unchecked(0UL - P1);
            int limit = end - 32;
            while (p <= limit)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p)));
                p += 8;
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p)));
                p += 8;
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p)));
                p += 8;
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p)));
                p += 8;
            }

            hash = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = P5;
        }

        hash += (ulong)length;
        while (p + 8 <= end)
        {
            hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p)));
            hash = Rotl(hash, 27) * P1 + P4;
            p += 8;
        }

        if (p + 4 <= end)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)) * P1;
            hash = Rotl(hash, 23) * P2 + P3;
            p += 4;
        }

        while (p < end)
        {
            hash ^= data[p++] * P5;
            hash = Rotl(hash, 11) * P1;
        }

        hash ^= hash >> 33;
        hash *= P2;
        hash ^= hash >> 29;
        hash *= P3;
        hash ^= hash >> 32;
        return hash;
    }
}