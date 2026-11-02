using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XISOSharp.Gui.Views;

/// <summary>
/// Minimal dark-themed message box with an accept and a cancel action,
/// matching the main window's palette. Used for the startup update
/// notification.
/// </summary>
public partial class MessageBoxWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxWindow"/> class.
    /// </summary>
    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    private MessageBoxWindow(string title, string message, string acceptText, string cancelText)
        : this()
    {
        Title = title;
        MessageTextBlock.Text = message;
        AcceptButton.Content = acceptText;
        CancelButton.Content = cancelText;
    }

    /// <summary>
    /// Shows a modal message box and returns whether the accept action was chosen.
    /// </summary>
    /// <param name="owner">Owning window for centering and modality.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message body.</param>
    /// <param name="acceptText">Accept button caption.</param>
    /// <param name="cancelText">Cancel button caption.</param>
    /// <returns><c>true</c> when the accept button was clicked; otherwise <c>false</c>.</returns>
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string acceptText, string cancelText)
    {
        MessageBoxWindow box = new(title, message, acceptText, cancelText);
        return await box.ShowDialog<bool>(owner);
    }

    private void Accept_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
