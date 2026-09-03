using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using XISOSharp.Gui.ViewModels;

namespace XISOSharp.Gui.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ImageFilter = new("Xbox images")
    {
        Patterns = ["*.iso", "*.xiso", "*.cso", "*.zar", "*.img"],
    };
    private static readonly FilePickerFileType CsoFilter = new("CISO images")
    {
        Patterns = ["*.cso", "*.1.cso"],
    };
    private static readonly FilePickerFileType IsoFilter = new("ISO images")
    {
        Patterns = ["*.iso", "*.xiso"],
    };

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private void LogBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.CaretIndex = int.MaxValue;
        }
    }

    private async Task<string?> PickSingleFileAsync(IReadOnlyList<FilePickerFileType> filters, string title)
    {
        var files = await PickFilesAsync(filters, title, allowMultiple: false).ConfigureAwait(false);
        return files.Count > 0 ? files[0] : null;
    }

    private async Task<List<string>> PickFilesAsync(IReadOnlyList<FilePickerFileType> filters, string title, bool allowMultiple)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return [];
        }

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [.. filters, FilePickerFileTypes.All],
        }).ConfigureAwait(false);
        return picked.Select(f => f.Path.LocalPath).ToList();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(false);
        return picked.Count > 0 ? picked[0].Path.LocalPath : null;
    }

    private async Task<string?> PickSaveAsync(string title, string? suggestedName)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        }).ConfigureAwait(false);
        return picked?.Path.LocalPath;
    }

    private static string AppendLines(string current, IEnumerable<string> added)
    {
        var lines = current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.AddRange(added);
        return string.Join(Environment.NewLine, lines);
    }

    private async void BrowseExImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([ImageFilter], "Select image").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.ExImage = picked;
        }
    }

    private async void BrowseExDest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.ExDest = picked;
        }
    }

    private async void BrowseCrSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.CrSource = picked;
        }
    }

    private async void AddRwImages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true).ConfigureAwait(false);
        Vm.RwImages = AppendLines(Vm.RwImages, picked);
    }

    private async void BrowseRwOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("Rewrite output", "rewritten.iso").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.RwOutput = picked;
        }
    }

    private async void BrowseRwWorkDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select work directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.RwWorkDir = picked;
        }
    }

    private async void BrowseRwReport_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.RwReport = picked;
        }
    }

    private async void BrowseWpImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([IsoFilter], "Select image").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.WpImage = picked;
        }
    }

    private async void AddRbParts_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFilesAsync([ImageFilter], "Add rebuild components", allowMultiple: true).ConfigureAwait(false);
        Vm.RbParts = AppendLines(Vm.RbParts, picked);
    }

    private async void BrowseRbOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("Redump output", "redump.iso").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.RbOutput = picked;
        }
    }

    private async void BrowseRbSectors_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([FilePickerFileTypes.TextPlain], "Select sectors file").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.RbSectors = picked;
        }
    }

    private async void BrowseCpSourceFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([IsoFilter], "Select source image").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.CpSource = picked;
        }
    }

    private async void BrowseCpSourceFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select source directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.CpSource = picked;
        }
    }

    private async void BrowseCpOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("CSO output", "game.cso").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.CpOutput = picked;
        }
    }

    private async void BrowseDcCso_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([CsoFilter], "Select CSO").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.DcCso = picked;
        }
    }

    private async void BrowseDcOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("ISO output", "game.iso").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.DcOutput = picked;
        }
    }

    private async void BrowseVaSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([IsoFilter], "Select source ISO").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.VaSource = picked;
        }
    }

    private async void BrowseVaOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([IsoFilter], "Select output ISO").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.VaOutput = picked;
        }
    }

    private async void BrowseVaReport_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSaveAsync("Validation report", "report.json").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.VaReport = picked;
        }
    }

    private async void AddCsImages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFilesAsync([ImageFilter], "Add images", allowMultiple: true).ConfigureAwait(false);
        Vm.CsImages = AppendLines(Vm.CsImages, picked);
    }

    private async void BrowseBaDir_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select batch directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.BaDir = picked;
        }
    }

    private async void BrowseBaDest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickFolderAsync("Select destination directory").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.BaDest = picked;
        }
    }

    private async void BrowseCliPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await PickSingleFileAsync([], "Select XISOSharp executable").ConfigureAwait(false);
        if (picked is not null)
        {
            Vm.CliPath = picked;
        }
    }
}
