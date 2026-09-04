using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using Serilog;
using XISOSharpTester.Logging;
using XISOSharpTester.Models;
using XISOSharpTester.ViewModels;

namespace XISOSharpTester.Views;

/// <summary>
/// Main test page. Creates its <see cref="ViewModels.MainViewModel"/> and auto-scrolls the log view.
/// </summary>
internal partial class MainPage
{
    /// <summary>
    /// Initializes the page and wires it to a new <see cref="ViewModels.MainViewModel"/>.
    /// </summary>
    public MainPage()
    {
        try
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            Log.Information("MainPage initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainPage initialization failed");
            BugReporter.ReportException(ex, "MainPage initialization failed");
            throw;
        }
    }

    private void LogTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (sender is TextBox tb)
            {
                tb.ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Log auto-scroll failed");
            BugReporter.ReportException(ex, "Log auto-scroll failed");
        }
    }
}

#pragma warning disable MA0048 // File name must match type name — converter is scoped to this page

/// <summary>
/// Converts a <see cref="TestStatus"/> value to a single-character
/// display icon for use in WPF data binding scenarios.
/// </summary>
public class StatusIconConverter : IValueConverter
{
    /// <summary>
    /// Converts a <see cref="TestStatus"/> value to its corresponding
    /// icon character: "✓" for Passed, "✗" for Failed, "○" for Skipped,
    /// and "?" for any unknown value.
    /// </summary>
    [SuppressMessage("ReSharper", "NullnessAnnotationConflictWithJetBrainsAnnotations")]
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TestStatus status
            ? status switch
            {
                TestStatus.Passed => "\u2713",
                TestStatus.Failed => "\u2717",
                TestStatus.Skipped => "\u25CB",
                _ => "?"
            }
            : "?";
    }

    /// <summary>
    /// Not supported. Throws <see cref="NotSupportedException"/>.
    /// </summary>
    [SuppressMessage("ReSharper", "NullnessAnnotationConflictWithJetBrainsAnnotations")]
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}