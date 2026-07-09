using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PolyTerm.App.Views;

/// <summary>Modal single-text-field prompt dialog. Closes with the trimmed input, or <c>null</c> if cancelled/empty.</summary>
public sealed partial class InputDialog : Window
{
    public InputDialog() => InitializeComponent();

    public InputDialog(string title, string initial) : this()
    {
        Title             = title;
        PromptLabel.Text  = title + ":";
        InputBox.Text     = initial;
    }

    /// <summary>Focuses and selects all text in the input box once the dialog is shown.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InputBox.Focus();
        InputBox.SelectAll();
    }

    /// <summary>Closes with the trimmed input text, or <c>null</c> if it's empty/whitespace.</summary>
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        Close(string.IsNullOrEmpty(text) ? null : text);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
