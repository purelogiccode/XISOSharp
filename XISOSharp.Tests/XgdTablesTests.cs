namespace XISOSharp.Tests;

/// <summary>
/// Tests for <see cref="XgdTables"/> — Xbox disc geometry constants and helpers.
/// </summary>
[Collection("Sequential")]
public class XgdTablesTests
{
    [Fact]
    public void XisoOffset_ArrayLengthIsFour()
    {
        Assert.Equal(4, XgdTables.XisoOffset.Length);
    }

    [Fact]
    public void XisoOffset_ValuesMatchExpected()
    {
        Assert.Equal(0x18300000L, XgdTables.XisoOffset[0]);
        Assert.Equal(0x0FD90000L, XgdTables.XisoOffset[1]);
        Assert.Equal(0x89D80000L, XgdTables.XisoOffset[2]);
        Assert.Equal(0x02080000L, XgdTables.XisoOffset[3]);
        // Also verify decimal equivalents
        Assert.Equal(405798912L, XgdTables.XisoOffset[0]);
        Assert.Equal(265879552L, XgdTables.XisoOffset[1]);
        Assert.Equal(2312634368L, XgdTables.XisoOffset[2]);
        Assert.Equal(34078720L, XgdTables.XisoOffset[3]);
    }

    [Fact]
    public void XisoLength_ArrayLengthFourAndValues()
    {
        Assert.Equal(4, XgdTables.XisoLength.Length);
        Assert.Equal(0x1A2DB0000L, XgdTables.XisoLength[0]);
        Assert.Equal(0x1B3880000L, XgdTables.XisoLength[1]);
        Assert.Equal(0x0BF8A0000L, XgdTables.XisoLength[2]);
        Assert.Equal(0x204510000L, XgdTables.XisoLength[3]);
        Assert.Equal(7027228672L, XgdTables.XisoLength[0]);
        Assert.Equal(7307001856L, XgdTables.XisoLength[1]);
        Assert.Equal(3213492224L, XgdTables.XisoLength[2]);
        Assert.Equal(8662351872L, XgdTables.XisoLength[3]);
    }

    [Fact]
    public void RedumpIsoLength_ArrayLengthNineAndKnownValues()
    {
        Assert.Equal(9, XgdTables.RedumpIsoLength.Length);
        // Verify all known Redump sizes (decimal as in task example 7825162240)
        Assert.Equal(7838154752L, XgdTables.RedumpIsoLength[0]); // 0x1D330C000
        Assert.Equal(7825162240L, XgdTables.RedumpIsoLength[1]); // 0x1D26A8000 — example from prompt
        Assert.Equal(7838111744L, XgdTables.RedumpIsoLength[2]); // 0x1D3301800
        Assert.Equal(7834892288L, XgdTables.RedumpIsoLength[3]); // 0x1D2FEF800
        Assert.Equal(7835492352L, XgdTables.RedumpIsoLength[4]); // 0x1D3082000
        Assert.Equal(7838695424L, XgdTables.RedumpIsoLength[5]); // 0x1D3390000
        Assert.Equal(7836663808L, XgdTables.RedumpIsoLength[6]); // 0x1D31A0000
        Assert.Equal(8738854912L, XgdTables.RedumpIsoLength[7]); // 0x208E05800
        Assert.Equal(8738846720L, XgdTables.RedumpIsoLength[8]); // 0x208E03800
    }

    [Fact]
    public void VideoL0Length_ArrayLengthNineteenAndValues()
    {
        Assert.Equal(19, XgdTables.VideoL0Length.Length);
        Assert.Equal(0x7458000L, XgdTables.VideoL0Length[0]);
        Assert.Equal(0x0D58000L, XgdTables.VideoL0Length[1]);
        Assert.Equal(0xA8000L, XgdTables.VideoL0Length[2]);
        Assert.Equal(0x4B1D0000L, XgdTables.VideoL0Length[15]);
        Assert.Equal(0x1878000L, XgdTables.VideoL0Length[16]);
        Assert.Equal(0x1880000L, XgdTables.VideoL0Length[17]);
        Assert.Equal(0x1880000L, XgdTables.VideoL0Length[18]);
    }

    [Fact]
    public void VideoL1Length_ArrayLengthNineteenAndValues()
    {
        Assert.Equal(19, XgdTables.VideoL1Length.Length);
        Assert.Equal(0x73B4000L, XgdTables.VideoL1Length[0]);
        Assert.Equal(0x0050000L, XgdTables.VideoL1Length[1]);
        Assert.Equal(0x09800L, XgdTables.VideoL1Length[2]);
        Assert.Equal(0x4AFD0000L, XgdTables.VideoL1Length[15]);
        Assert.Equal(0x186D800L, XgdTables.VideoL1Length[16]);
        Assert.Equal(0x1875800L, XgdTables.VideoL1Length[17]);
        Assert.Equal(0x1873800L, XgdTables.VideoL1Length[18]);
    }

    [Fact]
    public void VideoLength_ArrayLengthNineteen()
    {
        Assert.Equal(19, XgdTables.VideoLength.Length);
        Assert.Equal(0xE80C000L, XgdTables.VideoLength[0]);
        Assert.Equal(0x0DA8000L, XgdTables.VideoLength[1]);
        Assert.Equal(0x961A0000L, XgdTables.VideoLength[15]);
        Assert.Equal(0x30E5800L, XgdTables.VideoLength[16]);
    }

    [Fact]
    public void WavePvd_ArrayLengthTwentyFourAndKnownEntries()
    {
        Assert.Equal(24, XgdTables.WavePvd.Length);
        Assert.Equal("2004083110334900", XgdTables.WavePvd[0]);
        Assert.Equal("2005100712184600", XgdTables.WavePvd[1]);
        Assert.Equal("2013082617000000", XgdTables.WavePvd[19]);
        Assert.Equal("2015042617000000", XgdTables.WavePvd[20]);
        Assert.Equal("2010121616000000", XgdTables.WavePvd[23]);
    }

    [Fact]
    public void GetXgdType_MapsCorrectly()
    {
        Assert.Equal(0, XgdTables.GetXgdType(0));
        Assert.Equal(0, XgdTables.GetXgdType(1));
        Assert.Equal(1, XgdTables.GetXgdType(2));
        Assert.Equal(1, XgdTables.GetXgdType(3));
        Assert.Equal(1, XgdTables.GetXgdType(4));
        Assert.Equal(1, XgdTables.GetXgdType(5));
        Assert.Equal(2, XgdTables.GetXgdType(6));
        Assert.Equal(3, XgdTables.GetXgdType(7));
        Assert.Equal(3, XgdTables.GetXgdType(8));
    }

    [Fact]
    public void GetXgdType_InvalidDefaultsToZero()
    {
        Assert.Equal(0, XgdTables.GetXgdType(-1));
        Assert.Equal(0, XgdTables.GetXgdType(99));
        Assert.Equal(0, XgdTables.GetXgdType(int.MaxValue));
    }

    [Fact]
    public void GetRedumpIsoTypeBySize_KnownSizesReturnCorrectIndex()
    {
        Assert.Equal(0, XgdTables.GetRedumpIsoTypeBySize(7838154752L));
        Assert.Equal(1, XgdTables.GetRedumpIsoTypeBySize(7825162240L));
        Assert.Equal(2, XgdTables.GetRedumpIsoTypeBySize(7838111744L));
        Assert.Equal(3, XgdTables.GetRedumpIsoTypeBySize(7834892288L));
        Assert.Equal(4, XgdTables.GetRedumpIsoTypeBySize(7835492352L));
        Assert.Equal(5, XgdTables.GetRedumpIsoTypeBySize(7838695424L));
        Assert.Equal(6, XgdTables.GetRedumpIsoTypeBySize(7836663808L));
        Assert.Equal(7, XgdTables.GetRedumpIsoTypeBySize(8738854912L));
        Assert.Equal(8, XgdTables.GetRedumpIsoTypeBySize(8738846720L));
        // Hex equivalents
        Assert.Equal(1, XgdTables.GetRedumpIsoTypeBySize(0x1D26A8000L));
    }

    [Fact]
    public void GetRedumpIsoTypeBySize_UnknownReturnsMinusOne()
    {
        Assert.Equal(-1, XgdTables.GetRedumpIsoTypeBySize(0));
        Assert.Equal(-1, XgdTables.GetRedumpIsoTypeBySize(12345));
        Assert.Equal(-1, XgdTables.GetRedumpIsoTypeBySize(9999999999L));
    }

    [Fact]
    public void GetVideoTypeBySize_KnownSizes()
    {
        Assert.Equal(0, XgdTables.GetVideoTypeBySize(0xE80C000L));
        Assert.Equal(1, XgdTables.GetVideoTypeBySize(0x0DA8000L));
        Assert.Equal(15, XgdTables.GetVideoTypeBySize(0x961A0000L));
        Assert.Equal(16, XgdTables.GetVideoTypeBySize(0x30E5800L));
        Assert.Equal(17, XgdTables.GetVideoTypeBySize(0x30F5800L));
        Assert.Equal(18, XgdTables.GetVideoTypeBySize(0x30F3800L));
    }

    [Fact]
    public void GetVideoTypeBySize_UnknownReturnsMinusOne()
    {
        Assert.Equal(-1, XgdTables.GetVideoTypeBySize(0));
        Assert.Equal(-1, XgdTables.GetVideoTypeBySize(999));
    }

    [Fact]
    public void GetXisoTypeBySize_KnownSizes()
    {
        Assert.Equal(0, XgdTables.GetXisoTypeBySize(0x1A2DB0000L));
        Assert.Equal(1, XgdTables.GetXisoTypeBySize(0x1B3880000L));
        Assert.Equal(2, XgdTables.GetXisoTypeBySize(0x0BF8A0000L));
        Assert.Equal(3, XgdTables.GetXisoTypeBySize(0x204510000L));
        Assert.Equal(0, XgdTables.GetXisoTypeBySize(7027228672L));
    }

    [Fact]
    public void GetXisoTypeBySize_UnknownReturnsMinusOne()
    {
        Assert.Equal(-1, XgdTables.GetXisoTypeBySize(0));
        Assert.Equal(-1, XgdTables.GetXisoTypeBySize(12345));
    }

    [Fact]
    public void GetRedumpLength_MapsVideoTypeCorrectly()
    {
        Assert.Equal(7838154752L, XgdTables.GetRedumpLength(0));
        Assert.Equal(7825162240L, XgdTables.GetRedumpLength(1));
        Assert.Equal(7838111744L, XgdTables.GetRedumpLength(2));
        Assert.Equal(7838695424L, XgdTables.GetRedumpLength(5));
        Assert.Equal(7838695424L, XgdTables.GetRedumpLength(6));
        Assert.Equal(7838695424L, XgdTables.GetRedumpLength(14));
        Assert.Equal(7836663808L, XgdTables.GetRedumpLength(15));
        Assert.Equal(8738854912L, XgdTables.GetRedumpLength(16));
        Assert.Equal(8738854912L, XgdTables.GetRedumpLength(17));
        Assert.Equal(8738846720L, XgdTables.GetRedumpLength(18));
        Assert.Equal(0, XgdTables.GetRedumpLength(-1));
        Assert.Equal(0, XgdTables.GetRedumpLength(99));
    }

    [Fact]
    public void GetXisoTypeFromVideo_MapsCorrectly()
    {
        Assert.Equal(0, XgdTables.GetXisoTypeFromVideo(0));
        Assert.Equal(0, XgdTables.GetXisoTypeFromVideo(1));
        Assert.Equal(1, XgdTables.GetXisoTypeFromVideo(2));
        Assert.Equal(1, XgdTables.GetXisoTypeFromVideo(14));
        Assert.Equal(2, XgdTables.GetXisoTypeFromVideo(15));
        Assert.Equal(3, XgdTables.GetXisoTypeFromVideo(16));
        Assert.Equal(3, XgdTables.GetXisoTypeFromVideo(17));
        Assert.Equal(3, XgdTables.GetXisoTypeFromVideo(18));
        Assert.Equal(0, XgdTables.GetXisoTypeFromVideo(-1));
        Assert.Equal(0, XgdTables.GetXisoTypeFromVideo(99));
    }

    [Fact]
    public void GetWave_NullStream_ReturnsMinusOne()
    {
        Assert.Equal(-1, XgdTables.GetWave(null, 5));
        Assert.Equal(-1, XgdTables.GetWave(null, 7));
        Assert.Equal(-1, XgdTables.GetWave(null, 0));
    }

    [Fact]
    public void GetWave_InvalidRedumpType_ReturnsMinusOne()
    {
        // Use a dummy file even if provided, invalid type should short-circuit
        var tmp = Path.Combine(Path.GetTempPath(), $"xgd_wave_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmp, new byte[0x9000]);
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(-1, XgdTables.GetWave(fs, 0));
            Assert.Equal(-1, XgdTables.GetWave(fs, 1));
            Assert.Equal(-1, XgdTables.GetWave(fs, 2));
            Assert.Equal(-1, XgdTables.GetWave(fs, 6));
            Assert.Equal(-1, XgdTables.GetWave(fs, 8));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GetWave_ValidPvd_ReturnsCorrectIndex()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"xgd_wave_pvd_{Guid.NewGuid():N}.bin");
        // Need at least 0x832D + 16 bytes
        var data = new byte[0x9000];
        // Write known PVD at offset 0x832D
        var pvd0 = System.Text.Encoding.ASCII.GetBytes("2004083110334900");
        Array.Copy(pvd0, 0, data, 0x832D, pvd0.Length);
        File.WriteAllBytes(tmp, data);
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(0, XgdTables.GetWave(fs, 5));
            Assert.Equal(0, XgdTables.GetWave(fs, 7));

            // Overwrite with another known PVD
            var pvd14 = System.Text.Encoding.ASCII.GetBytes("2011120716000000"); // index 14
            fs.Seek(0x832D, SeekOrigin.Begin);
            // Need to write via file, reopen for write
        }
        finally
        {
            File.Delete(tmp);
        }

        // Second case: write different PVD and re-test
        var tmp2 = Path.Combine(Path.GetTempPath(), $"xgd_wave_pvd2_{Guid.NewGuid():N}.bin");
        var data2 = new byte[0x9000];
        var pvd14b = System.Text.Encoding.ASCII.GetBytes("2011120716000000");
        Array.Copy(pvd14b, 0, data2, 0x832D, pvd14b.Length);
        File.WriteAllBytes(tmp2, data2);
        try
        {
            using var fs2 = new FileStream(tmp2, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(14, XgdTables.GetWave(fs2, 5));
        }
        finally
        {
            File.Delete(tmp2);
        }
    }

    [Fact]
    public void GetWave_UnknownPvd_ReturnsMinusOne()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"xgd_wave_unknown_{Guid.NewGuid():N}.bin");
        var data = new byte[0x9000];
        var unknown = System.Text.Encoding.ASCII.GetBytes("9999999999999999");
        Array.Copy(unknown, 0, data, 0x832D, unknown.Length);
        File.WriteAllBytes(tmp, data);
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(-1, XgdTables.GetWave(fs, 5));
            Assert.Equal(-1, XgdTables.GetWave(fs, 7));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GetVideoType_DirectMappingsWithoutWave()
    {
        // Types 0-4,6,8 don't need wave; null fs should still return correct
        Assert.Equal(0, XgdTables.GetVideoType(null, 0));
        Assert.Equal(1, XgdTables.GetVideoType(null, 1));
        Assert.Equal(2, XgdTables.GetVideoType(null, 2));
        Assert.Equal(3, XgdTables.GetVideoType(null, 3));
        Assert.Equal(4, XgdTables.GetVideoType(null, 4));
        Assert.Equal(15, XgdTables.GetVideoType(null, 6));
        Assert.Equal(18, XgdTables.GetVideoType(null, 8));
        Assert.Equal(-1, XgdTables.GetVideoType(null, 9));
        Assert.Equal(-1, XgdTables.GetVideoType(null, -1));
    }

    [Fact]
    public void GetVideoType_WithWaveNull_ReturnsFallback()
    {
        // For redump 5 and 7, wave=-1 leads to -1 for 5, 17 for 7
        Assert.Equal(-1, XgdTables.GetVideoType(null, 5));
        Assert.Equal(17, XgdTables.GetVideoType(null, 7));
    }

    [Fact]
    public void GetVideoType_WithWaveFile_ReturnsMappedValue()
    {
        // Create a file with wave 0 PVD (2004083110334900) => GetVideoType for redump 5 should be 2
        var tmp = Path.Combine(Path.GetTempPath(), $"xgd_vtype_{Guid.NewGuid():N}.bin");
        var data = new byte[0x9000];
        var pvd0 = System.Text.Encoding.ASCII.GetBytes("2004083110334900"); // wave 0
        Array.Copy(pvd0, 0, data, 0x832D, pvd0.Length);
        File.WriteAllBytes(tmp, data);
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(2, XgdTables.GetVideoType(fs, 5));
            // wave 0 for type 5 => video type 2, then GetRedumpLength => 7838111744 etc.
        }
        finally
        {
            File.Delete(tmp);
        }

        // Wave 23 is special case for redump 7 => returns 16
        var tmp2 = Path.Combine(Path.GetTempPath(), $"xgd_vtype2_{Guid.NewGuid():N}.bin");
        var data2 = new byte[0x9000];
        var pvd23 = System.Text.Encoding.ASCII.GetBytes("2010121616000000"); // index 23
        Array.Copy(pvd23, 0, data2, 0x832D, pvd23.Length);
        File.WriteAllBytes(tmp2, data2);
        try
        {
            using var fs2 = new FileStream(tmp2, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(16, XgdTables.GetVideoType(fs2, 7));
            // non-23 wave for type 7 => 17
            // Test with wave 0 file for type 7 => should be 17
            var tmp3 = Path.Combine(Path.GetTempPath(), $"xgd_vtype3_{Guid.NewGuid():N}.bin");
            var data3 = new byte[0x9000];
            Array.Copy(pvd0, 0, data3, 0x832D, pvd0.Length);
            File.WriteAllBytes(tmp3, data3);
            try
            {
                using var fs3 = new FileStream(tmp3, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.Equal(17, XgdTables.GetVideoType(fs3, 7));
            }
            finally { File.Delete(tmp3); }
        }
        finally { File.Delete(tmp2); }
    }
}