using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the credential-vault "Browse…" picker on <see cref="NewConnectionViewModel"/> — issue #73's
/// remaining scope item after the GUI on-switch (issue #73's first fix) and the KeePass/Bitwarden
/// unlock flow (issue #78) landed: a way to actually pick an existing vault entry for a connection
/// instead of typing a password by hand.
/// </summary>
public sealed class NewConnectionViewModelCredentialTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static (NewConnectionViewModel Vm, Mock<IProfileStore> Store) MakeViewModel(IReadOnlyList<ICredentialProvider>? providers = null)
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<ConnectionProfile, CancellationToken>((p, _) => Task.FromResult(p));

        var sessions = new Mock<ISessionManager>();

        string? reason = null;
        var sshDescriptor = new Mock<IProtocolDescriptor>();
        sshDescriptor.Setup(d => d.Id).Returns("ssh");
        sshDescriptor.Setup(d => d.DisplayName).Returns("SSH");
        sshDescriptor.Setup(d => d.DefaultPort).Returns(22);
        sshDescriptor.Setup(d => d.IsAvailable(out reason)).Returns(true);

        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([sshDescriptor.Object]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());

        var vm = new NewConnectionViewModel(store.Object, tree, registry.Object,
            credentialProviders: providers ?? []);
        return (vm, store);
    }

    [AvaloniaFact]
    public void HasCredentialProviders_ReflectsWhetherAnyWerePassedIn()
    {
        MakeViewModel().Vm.HasCredentialProviders.Should().BeFalse("no providers were passed in");

        var provider = new Mock<ICredentialProvider>();
        MakeViewModel([provider.Object]).Vm.HasCredentialProviders.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task BrowseCredentialCommand_OnSelection_SetsLabelAndSuppressesTheCredentialWarning()
    {
        var (vm, _) = MakeViewModel();
        vm.ShowCredentialWarning.Should().BeTrue("nothing's been entered yet");

        vm.OpenCredentialPickerAsync = () => Task.FromResult<CredentialPickResult?>(
            new CredentialPickResult(
                new CredentialSummary { ProviderId = "bitwarden", ItemRef = "item-1", DisplayName = "prod-server", Username = "root" },
                "Bitwarden"));

        await vm.BrowseCredentialCommand.ExecuteAsync(null);

        vm.SelectedCredentialLabel.Should().Be("Bitwarden: prod-server (root)");
        vm.ShowCredentialWarning.Should().BeFalse("a credential was picked from the vault");
        vm.Username.Should().Be("root", "the Username field was empty, so it should be pre-filled from the picked entry");
    }

    [AvaloniaFact]
    public async Task BrowseCredentialCommand_DoesNotOverwriteAnAlreadyTypedUsername()
    {
        var (vm, _) = MakeViewModel();
        vm.Username = "already-typed";

        vm.OpenCredentialPickerAsync = () => Task.FromResult<CredentialPickResult?>(
            new CredentialPickResult(
                new CredentialSummary { ProviderId = "bitwarden", ItemRef = "item-1", DisplayName = "prod-server", Username = "root" },
                "Bitwarden"));

        await vm.BrowseCredentialCommand.ExecuteAsync(null);

        vm.Username.Should().Be("already-typed");
    }

    [AvaloniaFact]
    public async Task BrowseCredentialCommand_Cancelled_LeavesStateUnchanged()
    {
        var (vm, _) = MakeViewModel();
        vm.OpenCredentialPickerAsync = () => Task.FromResult<CredentialPickResult?>(null);

        await vm.BrowseCredentialCommand.ExecuteAsync(null);

        vm.SelectedCredentialLabel.Should().BeNull();
        vm.ShowCredentialWarning.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClearCredentialCommand_RevertsAPickedSelection()
    {
        var (vm, _) = MakeViewModel();
        vm.OpenCredentialPickerAsync = () => Task.FromResult<CredentialPickResult?>(
            new CredentialPickResult(
                new CredentialSummary { ProviderId = "keepass", ItemRef = "uuid-1", DisplayName = "entry" }, "KeePass (KeePassRPC)"));
        await vm.BrowseCredentialCommand.ExecuteAsync(null);
        vm.ShowCredentialWarning.Should().BeFalse();

        vm.ClearCredentialCommand.Execute(null);

        vm.SelectedCredentialLabel.Should().BeNull();
        vm.ShowCredentialWarning.Should().BeTrue("clearing the pick should bring the warning back with nothing else entered");
    }

    [AvaloniaFact]
    public async Task SaveAsync_WithAPickedCredential_StoresItsReferenceOnTheProfile_AndNeverCallsTheCredentialWriter()
    {
        var (vm, store) = MakeViewModel();
        vm.Name = "prod";
        vm.Host = "prod.example.com";
        vm.Password = "some-password-that-should-be-ignored";

        ConnectionProfile? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, CancellationToken>((p, _) => saved = p)
            .Returns<ConnectionProfile, CancellationToken>((p, _) => Task.FromResult(p));

        vm.OpenCredentialPickerAsync = () => Task.FromResult<CredentialPickResult?>(
            new CredentialPickResult(
                new CredentialSummary { ProviderId = "bitwarden", ItemRef = "item-42", DisplayName = "prod-server", Username = "root" },
                "Bitwarden"));
        await vm.BrowseCredentialCommand.ExecuteAsync(null);

        await vm.SaveCommand.ExecuteAsync(null);

        saved.Should().NotBeNull();
        saved!.Credential.Should().Be(new CredentialReference("bitwarden", "item-42"),
            "a credential picked via Browse should be stored directly, bypassing ICredentialWriter entirely");
    }
}
