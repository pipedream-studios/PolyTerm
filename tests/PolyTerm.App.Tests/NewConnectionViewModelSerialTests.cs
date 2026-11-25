using System.Text.Json;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>The serial "Flow Control" choice in the connection editor (issue #342).</summary>
public sealed class NewConnectionViewModelSerialTests
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
        var serial = new Mock<IProtocolDescriptor>();
        serial.Setup(d => d.Id).Returns("serial");
        serial.Setup(d => d.DisplayName).Returns("Serial");
        serial.Setup(d => d.IsAvailable(out reason)).Returns(true);
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([serial.Object]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, new Mock<ISessionManager>().Object, registry.Object, new FakeComPortWatcher());
        var vm = new NewConnectionViewModel(store.Object, tree, registry.Object, existing,
            initialProtocolId: "serial", credentialProviders: []);
        vm.Name = "console";
        vm.SerialPortName = "COM9";
        return (vm, () => saved);
    }

    private static ConnectionProfile SerialProfile(string settingsJson) => new()
    {
        Name = "console", ProtocolId = "serial",
        Settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson)!,
    };

    [AvaloniaTheory]
    [InlineData("""{"portName":"COM9"}""", "None")]
    [InlineData("""{"portName":"COM9","flowControl":"XOnXOff"}""", "XON/XOFF")]
    [InlineData("""{"portName":"COM9","flowControl":"requesttosend"}""", "RTS/CTS")]
    [InlineData("""{"portName":"COM9","flowControl":"RequestToSendXOnXOff"}""", "RTS/CTS + XON/XOFF")]
    [InlineData("""{"portName":"COM9","flowControl":"sideways"}""", "None")]
    public void OpeningAProfile_SelectsItsFlowControl(string settingsJson, string label)
    {
        var (vm, _) = MakeEditor(SerialProfile(settingsJson));

        vm.SelectedFlowControl.DisplayName.Should().Be(label);
        vm.FlowControlOptions.Should().Contain(vm.SelectedFlowControl);
    }

    [AvaloniaFact]
    public async Task ANewConnection_SavesNoFlowControlByDefault()
    {
        var (vm, saved) = MakeEditor();

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings["flowControl"].GetString().Should().Be("None");
    }

    [AvaloniaFact]
    public async Task TheSelectedFlowControl_IsSavedUnderItsHandshakeName()
    {
        var (vm, saved) = MakeEditor();
        vm.SelectedFlowControl = vm.FlowControlOptions.Single(o => o.DisplayName == "RTS/CTS");

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings["flowControl"].GetString().Should().Be("RequestToSend");
    }
}
