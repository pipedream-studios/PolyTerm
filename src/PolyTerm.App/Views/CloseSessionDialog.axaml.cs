using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// Shown when a tab's "On Close" setting is "Ask" (issue #77 Phase 1 — session detach/reattach): lets
/// the user choose whether to disconnect the session or leave it running in the background to reopen
/// later, instead of always disconnecting outright. Closes with a <see cref="CloseSessionChoice"/>.
/// </summary>
public sealed partial class CloseSessionDialog : Window
{
    public CloseSessionDialog() => InitializeComponent();

    public CloseSessionDialog(string sessionName) : this() =>
        MessageText.Text = $"Close \"{sessionName}\"'s tab, or keep it running in the background so it can be reopened later?";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(CloseSessionChoice.Close);
    private void Detach_Click(object? sender, RoutedEventArgs e) => Close(CloseSessionChoice.Detach);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(CloseSessionChoice.Cancel);
}
