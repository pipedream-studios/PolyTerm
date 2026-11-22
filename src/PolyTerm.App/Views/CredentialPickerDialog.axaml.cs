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

    /// <summary>
    /// Non-null while there's no item list to filter: loading, provider unavailable, or the load failed.
    /// <see cref="ApplySearchFilter"/> shows this instead of "No entries found.", since clearing the search
    /// box on a provider switch raises TextChanged through the dispatcher and can run the filter after
    /// the load has already finished.
    /// </summary>
    private string? _loadStatus;

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
            SetLoadStatus("");
            return;
        }

        var provider = _providers[ProviderCombo.SelectedIndex];
        SetLoadStatus("Loading…");

        // This runs from an async void event handler, so anything that escapes here reaches the UI
        // thread unhandled and terminates the app (issue #200: one undecryptable Bitwarden item did
        // exactly that). Show the failure in the dialog instead.
        try
        {
            var available = await provider.IsAvailableAsync().ConfigureAwait(true);
            if (!available)
            {
                SetLoadStatus($"{provider.DisplayName} is not reachable or unlocked right now.");
                return;
            }

            _allItems = await provider.BrowseAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _allItems = [];
            SetLoadStatus($"Couldn't load entries from {provider.DisplayName}: {ex.Message}");
            return;
        }

        _loadStatus = null;
        ApplySearchFilter();
    }

    private void SetLoadStatus(string status)
    {
        _loadStatus = status;
        StatusText.Text = status;
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
        if (_loadStatus is not null)
        {
            StatusText.Text = _loadStatus;
            return;
        }

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
