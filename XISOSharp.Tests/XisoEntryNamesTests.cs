namespace XISOSharp.Tests;

/// <summary>
/// Host-safety rules for image entry names: Windows reserved device names must
/// be rejected/sanitized like path separators and trailing dots.
/// </summary>
public class XisoEntryNamesTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CON.txt")]
    [InlineData("PRN")]
    [InlineData("AUX.log")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9.bin")]
    [InlineData("LPT1")]
    [InlineData("LPT9.txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    public void ReservedWindowsNames_AreRejectedAndSanitized(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(XisoEntryNames.IsReservedWindowsName(name));
        Assert.True(XisoEntryNames.IsInvalidEntryName(name));
        string sanitized = XisoEntryNames.SanitizeEntryName(name);
        Assert.False(XisoEntryNames.IsReservedWindowsName(sanitized));
        Assert.Equal(name.Length, sanitized.Length);
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("LPT10")]
    [InlineData("MYCON")]
    [InlineData("readme.txt")]
    [InlineData("console.txt")]
    public void OrdinaryNames_AreNotReserved(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(XisoEntryNames.IsReservedWindowsName(name));
        Assert.False(XisoEntryNames.IsInvalidEntryName(name));
        Assert.Equal(name, XisoEntryNames.SanitizeEntryName(name));
    }
}
