using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// Lets the user pick a detached (still-live, tab-less) session to reopen a tab for (issue #77 Phase 1
/// — session detach/reattach). Closes with the chosen <see cref="DetachedSessionItem"/>, or <c>null</c>
/// if cancelled.
/// </summary>
public sealed partial class DetachedSessionsDialog : Window
{
    public DetachedSessionsDialog() => InitializeComponent();

    public DetachedSessionsDialog(IReadOnlyList<DetachedSessionItem> items) : this() =>
        ItemsList.ItemsSource = items;

    private void ItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ReattachButton.IsEnabled = ItemsList.SelectedItem is not null;

    private void ItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsList.SelectedItem is not null) CloseWithSelection();
    }

    private void Reattach_Click(object? sender, RoutedEventArgs e) => CloseWithSelection();

    private void CloseWithSelection()
    {
        if (ItemsList.SelectedItem is DetachedSessionItem item) Close(item);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
