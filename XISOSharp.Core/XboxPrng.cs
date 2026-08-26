using System.Collections.Concurrent;

namespace XISOSharp;

/// <summary>
/// Xbox PRNG for XGD1 filler generation, ported from <c>References/XboxKit-0.7/LibXGD/XboxPRNG.cs</c>.
/// </summary>
public sealed class XboxPrng
{
    private static readonly uint[] FixedSeeds =
    [
        0x52F690D5u, 0x534D7DDEu, 0x5B71A70Fu, 0x66793320u, 0x9B7E5ED5u, 0xA465265Eu, 0xA53F1D11u, 0xB154430Fu
    ];

    private uint _state;
    private readonly uint _mult;
    private readonly uint _mask;

    /// <summary>Initializes a new instance of the Xbox PRNG with the given seed.</summary>
    /// <param name="seed">Initial seed value used to derive multiplier and state.</param>
    public XboxPrng(uint seed)
    {
        _mult = FixedSeeds[seed & 7];
        _state = (uint)(((seed + 1UL) * _mult) % 0xFFFFFFFB);
        _mask = _state;
    }

    /// <summary>Advance state as if <paramref name="count"/> sectors had been generated (no output).</summary>
    public void SimulateSectors(long count)
    {
        for (long i = 0; i < count; i++)
        for (int j = 0; j < Constants.SectorSize; j += 2)
            _state = (uint)(((_state + 1UL) * _mult) % 0xFFFFFFFB);
    }

    /// <summary>Write <paramref name="count"/> PRNG sectors to <paramref name="fs"/>.</summary>
    public void WriteSectors(FileStream fs, long count)
        => WriteSectors((Stream)fs, count);

    /// <summary>Writes <paramref name="count"/> PRNG sectors to <paramref name="output"/>.</summary>
    /// <param name="output">Destination stream to write filler sectors to.</param>
    /// <param name="count">Number of 2048-byte sectors to generate.</param>
    public void WriteSectors(Stream output, long count)
    {
        byte[] sector = new byte[Constants.SectorSize];
        for (long i = 0; i < count; i++)
        {
            GenerateSector(sector);
            output.Write(sector, 0, Constants.SectorSize);
        }
    }

    private void GenerateSector(Span<byte> sector)
    {
        for (int j = 0; j < Constants.SectorSize; j += 2)
        {
            _state = (uint)(((_state + 1UL) * _mult) % 0xFFFFFFFB);
            ushort sample = (ushort)((_state ^ _mask) >> 8);
            sector[j] = (byte)sample;
            sector[j + 1] = (byte)(sample >> 8);
        }
    }

    private byte[] GenerateSector()
    {
        byte[] sector = new byte[Constants.SectorSize];
        GenerateSector(sector);
        return sector;
    }

    /// <summary>
    /// Extract initial seed from an XGD1 XISO at <paramref name="xisoOffset"/> (byte offset).
    /// Returns null if magic/version checks fail or brute-force fails.
    /// Mirrors <c>XboxPRNG.ExtractSeed</c>.
    /// </summary>
    public static uint? ExtractSeed(FileStream isoFs, long xisoOffset, bool quiet)
    {
        // Validate XGD1 magic at 0x10800 (second sector magic is "XBOX_DVD_LAYOUT_TOOL_SIG" prefix)
        // The original reads 24 bytes at headerOffset+0x800 ; here offset+0x10800
        Span<byte> magic = stackalloc byte[24];
        if (!TryReadAt(isoFs, xisoOffset + 0x10800, magic))
            return null;
        ReadOnlySpan<byte> magic2 = "XBOX_DVD_LAYOUT_TOOL_SIG"u8;
        if (!magic[..magic2.Length].SequenceEqual(magic2))
            return null;

        Span<byte> nextBuf = stackalloc byte[8];
        if (!TryReadAt(isoFs, xisoOffset + 0x10820, nextBuf))
            return null;
        int versionOffset = 0x10824;
        bool allZero = true;
        for (int k = 0; k < 8; k++)
            if (nextBuf[k] != 0)
            {
                allZero = false;
                break;
            }

        if (allZero) versionOffset += 0x10;

        Span<byte> versionBuf = stackalloc byte[2];
        if (!TryReadAt(isoFs, xisoOffset + versionOffset, versionBuf))
            return null;
        ushort version = (ushort)(versionBuf[0] | (versionBuf[1] << 8));
        if (version == 0) return null;
        if (!quiet) Logger.Log($"[INFO] XGD1 Version: {version}\n");

        byte[] firstSector = new byte[Constants.SectorSize * 2];
        if (!TryReadAtArray(isoFs, xisoOffset, firstSector))
            return null;
        if (TryGetSeed(firstSector, out uint seed))
            return seed;
        return null;
    }

    /// <summary>
    /// Extracts the initial seed from an XGD1 XISO at <paramref name="xisoOffset"/> by reading the file at <paramref name="isoPath"/>.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <param name="xisoOffset">Byte offset of the XISO partition.</param>
    /// <param name="quiet">When <c>true</c>, suppresses informational logging.</param>
    /// <returns>The recovered seed, or <c>null</c> if validation or brute-force fails.</returns>
    public static uint? ExtractSeed(string isoPath, long xisoOffset = 0, bool quiet = false)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        return ExtractSeed(fs, xisoOffset, quiet);
    }

    private static bool TryReadAt(FileStream fs, long offset, Span<byte> buf)
    {
        try { fs.Seek(offset, SeekOrigin.Begin); }
        catch { return false; }

        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf[total..]);
            if (n == 0) break;
            total += n;
        }

        return total == buf.Length;
    }

    private static bool TryReadAtArray(FileStream fs, long offset, byte[] buf)
    {
        try { fs.Seek(offset, SeekOrigin.Begin); }
        catch { return false; }

        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }

        return total == buf.Length;
    }

    /// <summary>Brute-force seed from the first 4096 bytes (2 sectors). Mirrors <c>TryGetSeed</c>.</summary>
    public static bool TryGetSeed(byte[] sector, out uint outSeed)
    {
        uint foundSeed = 0;
        bool seedFound = false;

        const long maxUInt32 = (long)uint.MaxValue + 1;
        var range = Partitioner.Create(0L, maxUInt32);
        Parallel.ForEach(range, (chunk, state) =>
        {
            for (long i = chunk.Item1; i < chunk.Item2; i++)
            {
                if (Volatile.Read(ref seedFound))
                    break;
                uint seedGuess = (uint)i;
                uint multGuess = FixedSeeds[seedGuess & 7];
                uint stateGuess = (uint)(((seedGuess + 1UL) * multGuess) % 0xFFFFFFFB);
                uint maskAttempt = stateGuess;
                bool match = true;
                for (int j = 0; j < Constants.SectorSize * 2; j += 2)
                {
                    stateGuess = (uint)(((stateGuess + 1UL) * multGuess) % 0xFFFFFFFB);
                    ushort sample = (ushort)((stateGuess ^ maskAttempt) >> 8);
                    if (sector[j] != (byte)sample || sector[j + 1] != (byte)(sample >> 8))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    Volatile.Write(ref foundSeed, seedGuess);
                    Volatile.Write(ref seedFound, true);
                    state.Stop();
                    break;
                }
            }
        });

        outSeed = foundSeed;
        return seedFound;
    }
}