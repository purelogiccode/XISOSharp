using XISOSharp.Models;

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

    [Fact]
    public void ResolveExtractionName_Sanitize_ReplacesSeparatorAndDisambiguates()
    {
        HashSet<string> used = new(StringComparer.Ordinal);

        Assert.True(XisoEntryNames.TryResolveExtractionName(
            "a/b.txt", ExtractNamePolicy.Sanitize, used, out string first, out bool renamed));
        Assert.True(renamed);
        Assert.Equal("a_b.txt", first);

        Assert.True(XisoEntryNames.TryResolveExtractionName(
            "a\\b.txt", ExtractNamePolicy.Sanitize, used, out string second, out renamed));
        Assert.True(renamed);
        Assert.Equal("a_b~1.txt", second);
    }

    [Fact]
    public void ResolveExtractionName_Skip_ReturnsFalseForUnsafeName()
    {
        HashSet<string> used = new(StringComparer.Ordinal);

        Assert.False(XisoEntryNames.TryResolveExtractionName(
            "a/b.txt", ExtractNamePolicy.Skip, used, out _, out _));
        Assert.Empty(used);
    }

    [Fact]
    public void ResolveExtractionName_Reject_ThrowsForUnsafeName()
    {
        HashSet<string> used = new(StringComparer.Ordinal);

        Assert.Throws<XisoFormatException>(() => XisoEntryNames.TryResolveExtractionName(
            "a/b.txt", ExtractNamePolicy.Reject, used, out _, out _));
        Assert.Empty(used);
    }

    [Fact]
    public void ResolveExtractionName_Sanitize_ReplacesWindowsInvalidPunctuation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        Assert.True(XisoEntryNames.TryResolveExtractionName(
            "a?b*c.txt", ExtractNamePolicy.Sanitize, used, out string resolved, out bool renamed));
        Assert.True(renamed);
        Assert.Equal("a_b_c.txt", resolved);
    }
}
