using Avalonia.Controls;
using Avalonia.Interactivity;
using XISOSharp.Gui.Logging;
using XISOSharp.Gui.Services;

namespace XISOSharp.Gui.Views;

/// <summary>
/// About dialog: application version, description, acknowledgements, and links
/// to the project pages.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AboutWindow"/> class and
    /// fills in the current application version.
    /// </summary>
    public AboutWindow()
    {
        InitializeComponent();
        AppVersionTextBlock.Text = $"Version: {ProductVersion()}";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void Hyperlink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            _ = UrlOpener.TryOpen(url);
        }
    }

    /// <summary>
    /// Reads the informational version MinVer stamps on the entry assembly,
    /// trimming the <c>+build</c> metadata for display.
    /// </summary>
    private static string ProductVersion()
    {
        string version = EnvironmentInfo.ApplicationVersion();
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
