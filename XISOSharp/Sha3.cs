namespace XISOSharp;

/// <summary>
/// Pure-managed SHA3-256 (FIPS 202) incremental hasher.
/// Fallback for <see cref="System.Security.Cryptography.IncrementalHash"/> SHA3-256,
/// which throws <see cref="PlatformNotSupportedException"/> on OSes without SHA3
/// support (Windows 10 CNG, OpenSSL 1.x): same NIST vectors, no OS dependency,
/// no extra NuGet package, trim/AOT-safe like <see cref="Lz4"/>.
/// </summary>
internal sealed class Sha3256 : IDisposable
{
    private const int RateBytes = 136; // 1088-bit rate for SHA3-256
    private const int DigestBytes = 32;
    private const byte DomainSuffix = 0x06; // SHA3 (Keccak with 0x01 would be original Keccak)

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
        0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
        0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
        0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
        0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    // Rotation offsets r[x + 5*y] (FIPS 202 Table 2).
    private static readonly int[] RotationOffsets =
    [
        0, 1, 62, 28, 27,
        36, 44, 6, 55, 20,
        3, 10, 43, 25, 39,
        41, 45, 15, 21, 8,
        18, 2, 61, 56, 14,
    ];

    private readonly ulong[] _state = new ulong[25];
    private readonly byte[] _buffer = new byte[RateBytes];
    private int _buffered;
    private bool _disposed;

    /// <summary>Appends data to the hash.</summary>
    public void AppendData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int offset = 0;
        while (offset < data.Length)
        {
            // Fill the rate block.
            int take = Math.Min(RateBytes - _buffered, data.Length - offset);
            data.Slice(offset, take).CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            offset += take;

            if (_buffered == RateBytes)
            {
                AbsorbBlock(_buffer);
                _buffered = 0;
            }
        }
    }

    /// <summary>Appends a segment of a byte array to the hash.</summary>
    public void AppendData(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
        {
            throw new ArgumentException("Offset and count exceed buffer length.");
        }

        AppendData(buffer.AsSpan(offset, count));
    }

    /// <summary>Appends a byte array to the hash.</summary>
    public void AppendData(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        AppendData(buffer.AsSpan());
    }

    /// <summary>Finalizes the hash, returns the 32-byte digest, and resets for reuse.</summary>
    public byte[] GetHashAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pad10*1 with SHA3 domain separation: 0x06 ... 0x80.
        Span<byte> pad = stackalloc byte[RateBytes - _buffered];
        pad.Clear();
        pad[0] = DomainSuffix;
        pad[^1] |= 0x80;
        for (int i = 0; i < pad.Length; i++)
        {
            _buffer[_buffered + i] = pad[i];
        }

        AbsorbBlock(_buffer);

        byte[] digest = new byte[DigestBytes];
        for (int i = 0; i < 4; i++)
        {
            ulong lane = _state[i];
            digest[(i * 8) + 0] = (byte)lane;
            digest[(i * 8) + 1] = (byte)(lane >> 8);
            digest[(i * 8) + 2] = (byte)(lane >> 16);
            digest[(i * 8) + 3] = (byte)(lane >> 24);
            digest[(i * 8) + 4] = (byte)(lane >> 32);
            digest[(i * 8) + 5] = (byte)(lane >> 40);
            digest[(i * 8) + 6] = (byte)(lane >> 48);
            digest[(i * 8) + 7] = (byte)(lane >> 56);
        }

        Reset();
        return digest;
    }

    /// <summary>Computes the SHA3-256 digest of a single buffer.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        using Sha3256 hasher = new();
        hasher.AppendData(data);
        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Clears the internal state and marks the hasher disposed; further appends
    /// or digest calls throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Reset();
            _disposed = true;
        }
    }

    private void Reset()
    {
        Array.Clear(_state, 0, _state.Length);
        Array.Clear(_buffer, 0, _buffer.Length);
        _buffered = 0;
    }

    private void AbsorbBlock(byte[] block)
    {
        // XOR the 136-byte (17-lane) block into the state, little-endian.
        for (int i = 0; i < RateBytes / 8; i++)
        {
            ulong lane = (ulong)block[(i * 8) + 0]
                         | ((ulong)block[(i * 8) + 1] << 8)
                         | ((ulong)block[(i * 8) + 2] << 16)
                         | ((ulong)block[(i * 8) + 3] << 24)
                         | ((ulong)block[(i * 8) + 4] << 32)
                         | ((ulong)block[(i * 8) + 5] << 40)
                         | ((ulong)block[(i * 8) + 6] << 48)
                         | ((ulong)block[(i * 8) + 7] << 56);
            _state[i] ^= lane;
        }

        KeccakF1600(_state);
    }

    private static void KeccakF1600(ulong[] a)
    {
        Span<ulong> c = stackalloc ulong[5];
        Span<ulong> d = stackalloc ulong[5];
        Span<ulong> b = stackalloc ulong[25];

        for (int round = 0; round < 24; round++)
        {
            // Theta.
            for (int x = 0; x < 5; x++)
            {
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            }

            for (int x = 0; x < 5; x++)
            {
                d[x] = c[(x + 4) % 5] ^ RotateLeft(c[(x + 1) % 5], 1);
            }

            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    a[x + (5 * y)] ^= d[x];
                }
            }

            // Rho + Pi: B[y, 2x+3y] = ROT(A[x,y], r[x,y]).
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    int src = x + (5 * y);
                    int dstX = y;
                    int dstY = ((2 * x) + (3 * y)) % 5;
                    b[dstX + (5 * dstY)] = RotateLeft(a[src], RotationOffsets[src]);
                }
            }

            // Chi: A[x,y] = B[x,y] ^ ((~B[x+1,y]) & B[x+2,y]).
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    a[x + (5 * y)] = b[x + (5 * y)]
                                     ^ ((~b[((x + 1) % 5) + (5 * y)]) & b[((x + 2) % 5) + (5 * y)]);
                }
            }

            // Iota.
            a[0] ^= RoundConstants[round];
        }
    }

    private static ulong RotateLeft(ulong value, int shift)
    {
        shift &= 63;
        return shift == 0 ? value : (value << shift) | (value >> (64 - shift));
    }
}
