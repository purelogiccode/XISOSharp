using System.Text;

namespace XISOSharp;

/// <summary>
/// Xbox disc geometry tables ported from <c>References/XboxKit-0.7/LibXGD/XGD.cs:11</c>.
/// All values are byte lengths. Keep verbatim for Redump round-trip fidelity.
/// </summary>
public static class XgdTables
{
    /// <summary>Byte offsets of the XISO partition for each XGD type (XGD1, XGD2, XGD2-Hybrid, XGD3).</summary>
    public static readonly long[] XisoOffset = [0x18300000L, 0x0FD90000L, 0x89D80000L, 0x02080000L];

    /// <summary>Byte lengths of the XISO partition for each XGD type.</summary>
    public static readonly long[] XisoLength = [0x1A2DB0000L, 0x1B3880000L, 0x0BF8A0000L, 0x204510000L];

    /// <summary>Byte lengths of Redump ISO images for each Redump type.</summary>
    public static readonly long[] RedumpIsoLength =
    [
        0x1D330C000L, 0x1D26A8000L, 0x1D3301800L, 0x1D2FEF800L, 0x1D3082000L, 0x1D3390000L, 0x1D31A0000L, 0x208E05800L,
        0x208E03800L
    ];

    /// <summary>Byte lengths of the video L0 head for each video type.</summary>
    public static readonly long[] VideoL0Length =
    [
        0x7458000L, 0x0D58000L, 0xA8000L, 0x548000L, 0x438000L, 0x4BB0000L, 0x56C0000L, 0x5460000L, 0x5BA0000L,
        0x5C10000L,
        0x55D0000L, 0x55C0000L, 0x8A40000L, 0x8A90000L, 0x8E80000L, 0x4B1D0000L, 0x1878000L, 0x1880000L, 0x1880000L
    ];

    /// <summary>Byte lengths of the video L1 tail for each video type.</summary>
    public static readonly long[] VideoL1Length =
    [
        0x73B4000L, 0x0050000L, 0x09800L, 0x197800L, 0x11A000L, 0x4BA0000L, 0x56B0000L, 0x5450000L, 0x5B90000L,
        0x5C00000L,
        0x55C0000L, 0x55B0000L, 0x8A30000L, 0x8A80000L, 0x8E70000L, 0x4AFD0000L, 0x186D800L, 0x1875800L, 0x1873800L
    ];

    /// <summary>Total byte lengths of video partitions for each video type.</summary>
    public static readonly long[] VideoLength =
    [
        0xE80C000L, 0x0DA8000L, 0xB1800L, 0x6DF800L, 0x552000L, 0x9750000L, 0xAD70000L, 0xA8B0000L, 0xB730000L,
        0xB810000L,
        0xAB90000L, 0xAB70000L, 0x11470000L, 0x11510000L, 0x11CF0000L, 0x961A0000L, 0x30E5800L, 0x30F5800L, 0x30F3800L
    ];

    /// <summary>PVD creation datetimes used to identify XGD2 waves and XGD3 variants.</summary>
    public static readonly string[] WavePvd =
    [
        "2004083110334900", "2005100712184600", "2006030621090700", "2009011416000000", "2009082417000000",
        "2009100517000000", "2009102917000000", "2010022116000000", "2010090417000000", "2010091517000000",
        "2010102817000000", "2011011816000000", "2011061217000000", "2011071217000000", "2011120716000000",
        "2012022116000000", "2012062117000000", "2012110716000000", "2012111816000000", "2013082617000000",
        "2015042617000000", "2006041012132800", "2001091310425500", "2010121616000000"
    ];

    /// <summary>Maps Redump ISO type (0..8) to XGD type (0..3).</summary>
    public static int GetXgdType(int redumpIsoType)
        => redumpIsoType switch
        {
            0 or 1 => 0,
            2 or 3 or 4 or 5 => 1,
            6 => 2,
            7 or 8 => 3,
            _ => 0,
        };

    /// <summary>Gets video type index for a Redump ISO, or -1 if unknown. Mirrors XGD.GetVideoType.</summary>
    public static int GetVideoType(FileStream? isoFs, int redumpIsoType)
    {
        var wave = -1;
        if (redumpIsoType == 5 || redumpIsoType == 7)
            wave = GetWave(isoFs, redumpIsoType);

        return redumpIsoType switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => wave switch
            {
                0 => 2,
                1 => 3,
                2 => 4,
                3 => 5,
                4 or 5 or 6 or 7 => 6,
                8 or 9 => 7,
                10 or 11 or 12 => 8,
                13 => 9,
                14 or 15 => 10,
                16 => 11,
                17 or 18 => 12,
                19 => 13,
                20 => 14,
                21 => 15,
                22 => 0,
                _ => -1,
            },
            6 => 15,
            7 => wave switch
            {
                23 => 16,
                _ => 17,
            },
            8 => 18,
            _ => -1,
        };
    }

    /// <summary>Reads PVD at 0x832D and indexes into WAVE_PVD; -1 if not XGD2w3/XGD3v0 or read fails.</summary>
    public static int GetWave(FileStream? isoFs, int redumpIsoType)
    {
        if (isoFs is null) return -1;
        if (redumpIsoType != 5 && redumpIsoType != 7) return -1;
        try
        {
            isoFs.Seek(0x832D, SeekOrigin.Begin);
            Span<byte> pvd = stackalloc byte[16];
            // Use loop to handle partial reads.
            var total = 0;
            while (total < 16)
            {
                var n = isoFs.Read(pvd[total..]);
                if (n == 0) break;
                total += n;
            }

            if (total == 16)
                return Array.IndexOf(WavePvd, Encoding.ASCII.GetString(pvd));
        }
        catch
        {
            // ignored
        }

        return -1;
    }

    /// <summary>Gets the Redump ISO length for the given video type.</summary>
    /// <param name="videoType">Video type index.</param>
    /// <returns>Redump ISO byte length, or 0 if unknown.</returns>
    public static long GetRedumpLength(int videoType)
        => videoType switch
        {
            0 => RedumpIsoLength[0],
            1 => RedumpIsoLength[1],
            2 => RedumpIsoLength[2],
            3 => RedumpIsoLength[3],
            4 => RedumpIsoLength[4],
            5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 => RedumpIsoLength[5],
            15 => RedumpIsoLength[6],
            16 or 17 => RedumpIsoLength[7],
            18 => RedumpIsoLength[8],
            _ => 0,
        };

    /// <summary>Maps a video type index to its corresponding XISO type.</summary>
    /// <param name="videoType">Video type index.</param>
    /// <returns>XISO type (0=XGD1, 1=XGD2, 2=Hybrid, 3=XGD3).</returns>
    public static int GetXisoTypeFromVideo(int videoType)
        => videoType switch
        {
            0 or 1 => 0,
            2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 => 1,
            15 => 2,
            16 or 17 or 18 => 3,
            _ => 0,
        };

    /// <summary>Finds Redump ISO type by exact file size, or -1 if not a known Redump size.</summary>
    public static int GetRedumpIsoTypeBySize(long size) => Array.IndexOf(RedumpIsoLength, size);

    /// <summary>Finds video type by exact file size, or -1 if not a known video size.</summary>
    public static int GetVideoTypeBySize(long size) => Array.IndexOf(VideoLength, size);

    /// <summary>Finds XISO type by exact file size, or -1 if not a known XISO size.</summary>
    public static int GetXisoTypeBySize(long size) => Array.IndexOf(XisoLength, size);
}