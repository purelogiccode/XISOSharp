using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

using XISOSharpTester.Models;
using XISOSharpTester.ViewModels;

namespace XISOSharpTester.Views;

internal partial class MainPage
{
    public MainPage()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void LogTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.ScrollToEnd();
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