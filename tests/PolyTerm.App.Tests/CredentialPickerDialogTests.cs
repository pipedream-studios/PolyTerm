using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Views;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the search box added to <see cref="CredentialPickerDialog"/> — a growing vault (KeePass,
/// Bitwarden) can easily have more entries than fit comfortably in the picker's fixed-height list, with
/// no way to narrow it down before this.
/// </summary>
public sealed class CredentialPickerDialogTests
{
    private static Mock<ICredentialProvider> MakeProvider(params CredentialSummary[] items)
    {
        var provider = new Mock<ICredentialProvider>();
        provider.Setup(p => p.DisplayName).Returns("Test Provider");
        provider.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider.Setup(p => p.BrowseAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<CredentialSummary>)items);
        return provider;
    }

    private static CredentialPickerDialog OpenWithItems(params CredentialSummary[] items) =>
        new([MakeProvider(items).Object]);

    /// <summary>
    /// Setting TextBox.Text directly (not simulated keyboard input) still raises TextChanged through
    /// Avalonia's dispatcher queue rather than fully synchronously - an empty InvokeAsync flushes it,
    /// same convention already used elsewhere in this test project (ConnectionTreeViewModelTests,
    /// SessionTabViewModelResizeTests) for exactly this reason.
    /// </summary>
    private static async Task SetSearchTextAsync(CredentialPickerDialog window, string text)
    {
        window.FindControl<TextBox>("SearchBox")!.Text = text;
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    private static IReadOnlyList<CredentialSummary> VisibleItems(CredentialPickerDialog window) =>
        ((IEnumerable<CredentialSummary>)window.FindControl<ListBox>("ItemsList")!.ItemsSource!).ToList();

    [AvaloniaFact]
    public async Task SearchBox_FiltersByDisplayName()
    {
        var window = OpenWithItems(
            new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "prod-server", Username = "root" },
            new CredentialSummary { ProviderId = "p", ItemRef = "2", DisplayName = "staging-server", Username = "root" });

        await SetSearchTextAsync(window, "prod");

        VisibleItems(window).Should().ContainSingle(i => i.DisplayName == "prod-server");
    }

    [AvaloniaFact]
    public async Task SearchBox_FiltersByUsername()
    {
        var window = OpenWithItems(
            new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "server-a", Username = "alice" },
            new CredentialSummary { ProviderId = "p", ItemRef = "2", DisplayName = "server-b", Username = "bob" });

        await SetSearchTextAsync(window, "alice");

        VisibleItems(window).Should().ContainSingle(i => i.Username == "alice");
    }

    [AvaloniaFact]
    public async Task SearchBox_IsCaseInsensitive()
    {
        var window = OpenWithItems(new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "Prod-Server" });

        await SetSearchTextAsync(window, "PROD");

        VisibleItems(window).Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task SearchBox_WithNoMatches_ShowsAMatchSpecificMessage_NotTheEmptyVaultMessage()
    {
        var window = OpenWithItems(new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "prod-server" });

        await SetSearchTextAsync(window, "nothing-matches-this");

        VisibleItems(window).Should().BeEmpty();
        window.FindControl<TextBlock>("StatusText")!.Text.Should().Be("No entries match your search.");
    }

    [AvaloniaFact]
    public async Task ClearingTheSearchBox_RestoresTheFullList()
    {
        var window = OpenWithItems(
            new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "prod-server" },
            new CredentialSummary { ProviderId = "p", ItemRef = "2", DisplayName = "staging-server" });

        await SetSearchTextAsync(window, "prod");
        VisibleItems(window).Should().ContainSingle();

        await SetSearchTextAsync(window, "");

        VisibleItems(window).Should().HaveCount(2);
    }
}
