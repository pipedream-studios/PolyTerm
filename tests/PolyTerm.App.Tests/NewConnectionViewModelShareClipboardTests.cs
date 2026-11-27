using System.Text.Json;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>The RDP "Share clipboard" option (issue #390): on by default, with only "off" saved.</summary>
public sealed class NewConnectionViewModelShareClipboardTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private static (NewConnectionViewModel Vm, Func<ConnectionProfile?> Saved) MakeEditor(ConnectionProfile? existing = null)
    {
        ConnectionProfile? saved = null;
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, CancellationToken>((p, _) => saved = p)
            .Returns<ConnectionProfile, CancellationToken>((p, _) => Task.FromResult(p));

        string? reason = null;
        var rdp = new Mock<IProtocolDescriptor>();
        rdp.Setup(d => d.Id).Returns("rdp");
        rdp.Setup(d => d.DisplayName).Returns("RDP");
        rdp.Setup(d => d.DefaultPort).Returns(3389);
        rdp.Setup(d => d.IsAvailable(out reason)).Returns(true);
        var ssh = new Mock<IProtocolDescriptor>();
        ssh.Setup(d => d.Id).Returns("ssh");
        ssh.Setup(d => d.DisplayName).Returns("SSH");
        ssh.Setup(d => d.DefaultPort).Returns(22);
        ssh.Setup(d => d.IsAvailable(out reason)).Returns(true);
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([rdp.Object, ssh.Object]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, new Mock<ISessionManager>().Object, registry.Object, new FakeComPortWatcher());
        var vm = new NewConnectionViewModel(store.Object, tree, registry.Object, existing,
            initialProtocolId: "rdp", credentialProviders: []);
        vm.Name = "win";
        vm.Host = "win.example.com";
        return (vm, () => saved);
    }

    /// <summary>An existing RDP profile whose settings are the given JSON object, as stored on disk.</summary>
    private static ConnectionProfile RdpProfile(string settingsJson) => new()
    {
        Name = "win", Host = "win.example.com", ProtocolId = "rdp",
        Settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson)!,
    };

    [AvaloniaFact]
    public async Task ANewConnection_SharesTheClipboard_AndSavesNothingExtra()
    {
        var (vm, saved) = MakeEditor();

        vm.ShareClipboard.Should().BeTrue();
        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().NotContainKey("shareClipboard");
    }

    [AvaloniaFact]
    public void AConnectionSavedBeforeTheOption_SharesTheClipboard()
    {
        var (vm, _) = MakeEditor(RdpProfile("""{ "username": "rob" }"""));

        vm.ShareClipboard.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TurningItOff_IsSaved_AndStillOffWhenReopened()
    {
        var (vm, saved) = MakeEditor();
        vm.ShareClipboard = false;

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings["shareClipboard"].ValueKind.Should().Be(JsonValueKind.False);
        var (reopened, _) = MakeEditor(saved());
        reopened.ShareClipboard.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task TurningItBackOn_RemovesTheSavedValue()
    {
        var (vm, saved) = MakeEditor(RdpProfile("""{ "shareClipboard": false }"""));
        vm.ShareClipboard = true;

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().NotContainKey("shareClipboard");
    }

    [AvaloniaFact]
    public async Task ItIsOnlySavedForRdp()
    {
        var (vm, saved) = MakeEditor();
        vm.ShareClipboard = false;
        vm.SelectedProtocol = vm.Protocols.Single(p => p.Id == "ssh");

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().NotContainKey("shareClipboard");
    }
}
