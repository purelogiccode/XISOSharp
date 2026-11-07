#if NET10_0_OR_GREATER
using XISOSharp.Gui.ViewModels;

namespace XISOSharp.Tests;

/// <summary>
/// Pure view-model checks for the bidirectional ZAR tab: a <c>.zar</c> source
/// switches the tab to convert-to-ISO mode, while an image source keeps the
/// pack-to-ZAR behavior and its collision policy.
/// </summary>
public class MainViewModelTests
{
    /// <summary>Verifies the ZAR tab direction, labels, and policy gating follow the source extension.</summary>
    [Fact]
    public void ZarTab_DirectionFollowsSource()
    {
        MainViewModel vm = new();

        Assert.False(vm.ZaIsConvert);
        Assert.Equal("Create ZAR", vm.ZaActionLabel);
        Assert.Equal("Output .zar", vm.ZaOutputLabel);
        Assert.True(vm.ZaCanPickPolicy);

        vm.ZaSource = " game.zar ";
        Assert.True(vm.ZaIsConvert);
        Assert.Equal("Convert to ISO", vm.ZaActionLabel);
        Assert.Equal("Output ISO", vm.ZaOutputLabel);
        Assert.False(vm.ZaCanPickPolicy);

        vm.ZaSource = "game.iso";
        Assert.False(vm.ZaIsConvert);
        Assert.Equal("Create ZAR", vm.ZaActionLabel);
    }

    /// <summary>Verifies the title-info panel formats XBE/XEX JSON fields.</summary>
    [Fact]
    public void FormatTitleInfo_XbeAndXexFields()
    {
        string formatted = MainViewModel.FormatTitleInfo(
            """
            {"kind":"xex","titleName":"XDBF Game","titleIdHex":"0x4D5307E6",
             "gameRegionName":"NTSC","discNumber":1,"discCount":2,"version":5,
             "xexPath":"/default.xex","publisher":"Pub","developer":"Dev",
             "genre":"Action","description":"Desc","hasThumbnail":true}
            """);

        Assert.Contains("Kind: xex", formatted, StringComparison.Ordinal);
        Assert.Contains("Title: XDBF Game", formatted, StringComparison.Ordinal);
        Assert.Contains("Title ID: 0x4D5307E6", formatted, StringComparison.Ordinal);
        Assert.Contains("Region: NTSC", formatted, StringComparison.Ordinal);
        Assert.Contains("Disc number: 1", formatted, StringComparison.Ordinal);
        Assert.Contains("Disc count: 2", formatted, StringComparison.Ordinal);
        Assert.Contains("XEX: /default.xex", formatted, StringComparison.Ordinal);
        Assert.Contains("Publisher: Pub", formatted, StringComparison.Ordinal);
        Assert.Contains("Thumbnail: yes", formatted, StringComparison.Ordinal);
    }

    /// <summary>Verifies GOD/STFS JSON fields and executable path extraction.</summary>
    [Fact]
    public void FormatTitleInfo_GodAndAssetPath()
    {
        string json =
            """
            {"kind":"god","titleName":"Halo 3","titleIdHex":"0x4D5307E6",
             "discNumber":1,"discCount":2,"headerPath":"4D5307E6"}
            """;

        string formatted = MainViewModel.FormatTitleInfo(json);
        Assert.Contains("Kind: god", formatted, StringComparison.Ordinal);
        Assert.Contains("Title: Halo 3", formatted, StringComparison.Ordinal);
        Assert.Contains("GOD header: 4D5307E6", formatted, StringComparison.Ordinal);
        Assert.Null(MainViewModel.TryGetTitleAssetPath(json));

        string xbe = MainViewModel.FormatTitleInfo(
            """{"kind":"xbe","titleName":"Game","xbePath":"/default.xbe"}""");
        Assert.Contains("XBE: /default.xbe", xbe, StringComparison.Ordinal);
        Assert.Equal("/default.xbe", MainViewModel.TryGetTitleAssetPath(
            """{"kind":"xbe","xbePath":"/default.xbe"}"""));
    }

    /// <summary>Verifies unparseable title output is shown verbatim.</summary>
    [Fact]
    public void FormatTitleInfo_Malformed_ReturnsRawText()
    {
        Assert.Equal("not json", MainViewModel.FormatTitleInfo("not json"));
        Assert.Equal("(no title metadata)", MainViewModel.FormatTitleInfo(" "));
    }

    /// <summary>Verifies only CISO/CCI sources enable the decompress action.</summary>
    [Fact]
    public void XbCanDecompress_FollowsSourceExtension()
    {
        MainViewModel vm = new();
        Assert.False(vm.XbCanDecompress);
        vm.XbSource = "game.GOD";
        Assert.False(vm.XbCanDecompress);
        vm.XbSource = "game.cso";
        Assert.True(vm.XbCanDecompress);
        vm.XbSource = "game.1.cci";
        Assert.True(vm.XbCanDecompress);
    }
}
#endif
