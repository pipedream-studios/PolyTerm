using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PolyTerm.App.Views;

/// <summary>Modal yes/no confirmation dialog. Closes with <c>true</c> (confirm) or <c>false</c> (cancel).</summary>
public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    /// <param name="actionLabel">Text for the confirming button. Defaults to "Delete" (the dialog's original, and still most common, use — SFTP delete confirmations).</param>
    /// <param name="destructive">When true (the default), the confirming button uses the red "destructive action" styling declared in the .axaml. Set false for non-destructive confirmations (e.g. the MCP approval gate) to use the plain default button style instead.</param>
    public ConfirmDialog(string title, string message, string actionLabel = "Delete", bool destructive = true) : this()
    {
        Title                 = title;
        MessageLabel.Text     = message;
        ActionButton.Content  = actionLabel;

        if (!destructive)
        {
            ActionButton.ClearValue(Button.ForegroundProperty);
            ActionButton.ClearValue(Button.BackgroundProperty);
        }
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e)  => Close(false);
}
