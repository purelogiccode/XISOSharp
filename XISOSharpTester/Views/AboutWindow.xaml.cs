using System.Reflection;
using System.Windows;

namespace XISOSharpTester.Views;

internal partial class AboutWindow
{
    internal AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionTextBlock.Text = $"Version: {version?.ToString() ?? "Unknown"}";

        DescriptionTextBlock.Text =
            "A WPF desktop application for batch-testing XISO files using the XISOSharp library. " +
            "Cross-checks the C# XISO reader/writer against the original extract-xiso C tool, " +
            "with support for header verification, file listing comparison, per-file SHA-256 " +
            "hash comparison of extracted content, and XISO rewrite verification.";

        GitHubLink.NavigateUri = new Uri("https://github.com/XboxDev/extract-xiso");
        WebLink.NavigateUri = new Uri("https://github.com/XboxDev/extract-xiso");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open link: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
