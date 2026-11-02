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
}
#endif
