using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>
/// Lets the user browse every registered <see cref="ICredentialProvider"/> (OS keychain, and KeePass/
/// Bitwarden if enabled) and pick an existing vault entry for a connection profile, instead of typing a
/// password by hand — the "Browse…" picker <c>ICredentialProvider.BrowseAsync</c> was always meant to
/// back (issue #73's remaining scope item). Closes with a <see cref="CredentialPickResult"/>, or
/// <c>null</c> if cancelled.
/// </summary>
public sealed partial class CredentialPickerDialog : Window
{
    private IReadOnlyList<ICredentialProvider> _providers = [];

    /// <summary>The provider's full, unfiltered result from its last <see cref="ICredentialProvider.BrowseAsync"/> call — <see cref="ItemsList"/>'s source is always a filtered view over this, never re-queried per keystroke.</summary>
    private IReadOnlyList<CredentialSummary> _allItems = [];

    public CredentialPickerDialog() => InitializeComponent();

    public CredentialPickerDialog(IReadOnlyList<ICredentialProvider> providers) : this()
    {
        _providers = providers;
        ProviderCombo.ItemsSource = providers.Select(p => p.DisplayName).ToList();
        if (providers.Count > 0) ProviderCombo.SelectedIndex = 0;
    }

    private async void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SearchBox.Text = "";
        await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        _allItems = [];
        ItemsList.ItemsSource = null;
        SelectButton.IsEnabled = false;

        if (ProviderCombo.SelectedIndex < 0 || ProviderCombo.SelectedIndex >= _providers.Count)
        {
            StatusText.Text = "";
            return;
        }

        var provider = _providers[ProviderCombo.SelectedIndex];
        StatusText.Text = "Loading…";

        var available = await provider.IsAvailableAsync().ConfigureAwait(true);
        if (!available)
        {
            StatusText.Text = $"{provider.DisplayName} is not reachable or unlocked right now.";
            return;
        }

        _allItems = await provider.BrowseAsync().ConfigureAwait(true);
        ApplySearchFilter();
    }

    /// <summary>
    /// Filters the already-loaded <see cref="_allItems"/> by <see cref="SearchBox"/>'s text (a plain
    /// case-insensitive substring match against display name and username) - a client-side filter over
    /// data already fetched, not a re-query per keystroke, since <see cref="ICredentialProvider.BrowseAsync"/>
    /// can mean a real network round-trip (KeePassRPC, Bitwarden) that a growing vault makes worth
    /// avoiding on every keypress.
    /// </summary>
    private void SearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var query = SearchBox.Text?.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (i.Username?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        ItemsList.ItemsSource = filtered;
        StatusText.Text = _allItems.Count == 0
            ? "No entries found."
            : filtered.Count == 0
                ? "No entries match your search."
                : "";
    }

    private void ItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = ItemsList.SelectedItem is not null;

    private void ItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsList.SelectedItem is not null) CloseWithSelection();
    }

    private void Select_Click(object? sender, RoutedEventArgs e) => CloseWithSelection();

    private void CloseWithSelection()
    {
        if (ItemsList.SelectedItem is not CredentialSummary summary || ProviderCombo.SelectedIndex < 0) return;
        var provider = _providers[ProviderCombo.SelectedIndex];
        Close(new CredentialPickResult(summary, provider.DisplayName));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
