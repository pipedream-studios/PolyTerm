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

    /// <summary>Issue #285: typing an organization, collection or folder name narrows the list to entries in it.</summary>
    [AvaloniaFact]
    public async Task SearchBox_FiltersByLocation()
    {
        var window = OpenWithItems(
            new CredentialSummary { ProviderId = "p", ItemRef = "1", DisplayName = "server", Location = "My vault › Servers" },
            new CredentialSummary { ProviderId = "p", ItemRef = "2", DisplayName = "server", Location = "Automation › infra, health" });

        await SetSearchTextAsync(window, "infra");

        VisibleItems(window).Should().ContainSingle(i => i.ItemRef == "2");
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

    private static readonly CredentialSummary PasswordItem =
        new() { ProviderId = "p", ItemRef = "pw", DisplayName = "server", Username = "root" };

    private static readonly CredentialSummary KeyItem =
        new() { ProviderId = "p", ItemRef = "key", DisplayName = "deploy key", Kind = CredentialKind.SshKey, KeyFingerprint = "SHA256:abc123" };

    /// <summary>Issue #283: SSH key entries are meaningless for RDP, VNC, Telnet or Web, so the picker hides them unless asked.</summary>
    [AvaloniaFact]
    public async Task SshKeyEntries_AreHiddenByDefault()
    {
        var window = new CredentialPickerDialog([MakeProvider(PasswordItem, KeyItem).Object]);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        VisibleItems(window).Should().ContainSingle().Which.ItemRef.Should().Be("pw");
    }

    [AvaloniaFact]
    public async Task SshKeyEntries_AreListed_WhenIncluded_AndSearchableByFingerprint()
    {
        var window = new CredentialPickerDialog([MakeProvider(PasswordItem, KeyItem).Object], includeSshKeys: true);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        VisibleItems(window).Should().HaveCount(2);

        await SetSearchTextAsync(window, "abc123");

        VisibleItems(window).Should().ContainSingle().Which.ItemRef.Should().Be("key");
    }

    /// <summary>
    /// Issue #200: LoadItemsAsync runs from an async void SelectionChanged handler, so a BrowseAsync
    /// exception used to reach the UI thread unhandled and terminate the whole app. It should land in
    /// the dialog's status text instead.
    /// </summary>
    [AvaloniaFact]
    public async Task BrowseAsyncThrowing_ShowsTheErrorInTheDialog_InsteadOfCrashing()
    {
        var provider = new Mock<ICredentialProvider>();
        provider.Setup(p => p.DisplayName).Returns("Test Provider");
        provider.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        provider.Setup(p => p.BrowseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("MAC verification failed."));

        var window = new CredentialPickerDialog([provider.Object]);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        window.FindControl<TextBlock>("StatusText")!.Text.Should()
            .Be("Couldn't load entries from Test Provider: MAC verification failed.");
        window.FindControl<ListBox>("ItemsList")!.ItemsSource.Should().BeNull();
    }
}
