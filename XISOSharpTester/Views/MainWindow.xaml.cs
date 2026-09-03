using System.ComponentModel;
using System.Windows;
using XISOSharpTester.ViewModels;

namespace XISOSharpTester.Views;

internal partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (MainPageView.DataContext is MainViewModel { IsRunning: true })
        {
            var result = MessageBox.Show(
                "A test run is currently in progress. Are you sure you want to exit?",
                "Tests Running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }
}