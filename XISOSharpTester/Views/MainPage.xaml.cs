using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    private void LogTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.ScrollToEnd();
        }
    }
}

public class StatusIconConverter : IValueConverter
{
    [SuppressMessage("ReSharper", "NullnessAnnotationConflictWithJetBrainsAnnotations")]
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TestStatus status
            ? status switch
            {
                TestStatus.Passed => "✓",
                TestStatus.Failed => "✗",
                TestStatus.Skipped => "○",
                _ => "?"
            }
            : "?";
    }

    [SuppressMessage("ReSharper", "NullnessAnnotationConflictWithJetBrainsAnnotations")]
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
