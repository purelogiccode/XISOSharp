using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Serilog;
using XISOSharpTester.Logging;

namespace XISOSharpTester.Views;

/// <summary>
/// About dialog showing the assembly version, application description, and support links.
/// </summary>
internal partial class AboutWindow
{
    /// <summary>
    /// Initializes the dialog, populating the version text, description, and link URIs.
    /// </summary>
    internal AboutWindow()
    {
        try
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
        catch (Exception ex)
        {
            Log.Error(ex, "AboutWindow initialization failed");
            BugReporter.ReportException(ex, "AboutWindow initialization failed");
            throw;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open link {Uri}", e.Uri);
            BugReporter.ReportException(ex, $"Could not open link {e.Uri}");
            MessageBox.Show($"Could not open link: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}