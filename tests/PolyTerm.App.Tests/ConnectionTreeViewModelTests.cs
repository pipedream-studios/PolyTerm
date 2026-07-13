using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using PolyTerm.Protocols.Serial;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="ConnectionTreeViewModel"/>'s detected-USB-COM-port integration (issue #33):
/// the grouping rule (flat vs. a single folder above 4 ports), the "nothing at all when zero ports
/// are detected" rule, ad-hoc nodes routing through <see cref="ISessionManager.OpenDirectAsync"/>
/// instead of <see cref="ISessionManager.OpenAsync"/>, and picking up live changes via
/// <see cref="IComPortWatcher.PortsChanged"/>. Uses a fake <see cref="IComPortWatcher"/> — the real
/// WMI-based detection in <see cref="WmiUsbComPortDetector"/> isn't exercised here (no USB serial
/// hardware was available in this environment to verify it against; see that class's own doc comment).
/// </summary>
public sealed class ConnectionTreeViewModelTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<DetectedComPort> CurrentPorts { get; set; } = [];
        public event EventHandler? PortsChanged;
        public void RaisePortsChanged() => PortsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static (ConnectionTreeViewModel Tree, Mock<ISessionManager> Sessions, FakeComPortWatcher Watcher) MakeTree(
        IReadOnlyList<DetectedComPort> ports)
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);
        var watcher = new FakeComPortWatcher { CurrentPorts = ports };

        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, watcher);
        return (tree, sessions, watcher);
    }

    private static async Task LoadAndSettleAsync(ConnectionTreeViewModel tree)
    {
        await tree.LoadAsync();
        // LoadAsync's RootNodes assignment happens via Dispatcher.UIThread.Post, not synchronously -
        // an empty InvokeAsync round-trip guarantees anything already posted has run.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
    }

    [AvaloniaFact]
    public async Task LoadAsync_WithNoDetectedPorts_AddsNothingToTheTree()
    {
        var (tree, _, _) = MakeTree([]);

        await LoadAndSettleAsync(tree);

        tree.RootNodes.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task LoadAsync_WithFourOrFewerDetectedPorts_ListsThemFlat()
    {
        var ports = new[]
        {
            new DetectedComPort("COM3", "USB Serial Device (COM3)"),
            new DetectedComPort("COM5", "Silicon Labs CP210x (COM5)"),
        };
        var (tree, _, _) = MakeTree(ports);

        await LoadAndSettleAsync(tree);

        tree.RootNodes.Should().HaveCount(2);
        tree.RootNodes.Should().OnlyContain(n => n.IsAdHoc && !n.IsFolder);
        tree.RootNodes.Select(n => n.Profile.Host).Should().BeEquivalentTo("COM3", "COM5");
    }

    [AvaloniaFact]
    public async Task LoadAsync_WithMoreThanFourDetectedPorts_GroupsThemUnderOneFolder()
    {
        var ports = Enumerable.Range(1, 5).Select(i => new DetectedComPort($"COM{i}", $"Device (COM{i})")).ToArray();
        var (tree, _, _) = MakeTree(ports);

        await LoadAndSettleAsync(tree);

        var folder = tree.RootNodes.Should().ContainSingle().Subject;
        folder.IsFolder.Should().BeTrue();
        folder.IsAdHoc.Should().BeTrue();
        folder.Name.Should().Be("Detected COM Ports (5)");
        folder.Children.Should().HaveCount(5);
        folder.Children.Should().OnlyContain(c => c.IsAdHoc && !c.IsFolder);
    }

    [AvaloniaFact]
    public async Task DetectedPortNode_CannotBeEditedRenamedDeletedOrDuplicated()
    {
        var (tree, _, _) = MakeTree([new DetectedComPort("COM3", "USB Serial Device (COM3)")]);
        await LoadAndSettleAsync(tree);
        var node = tree.RootNodes.Should().ContainSingle().Subject;

        node.CanModify.Should().BeFalse();
        node.CanDuplicate.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ConnectingADetectedPortNode_UsesOpenDirectAsync_NotOpenAsync()
    {
        var (tree, sessions, _) = MakeTree([new DetectedComPort("COM3", "USB Serial Device (COM3)")]);
        await LoadAndSettleAsync(tree);
        var node = tree.RootNodes.Should().ContainSingle().Subject;

        await tree.ConnectNodeAsync(node);

        sessions.Verify(s => s.OpenDirectAsync(
            It.Is<ConnectionProfile>(p => p.Host == "COM3" && p.ProtocolId == "serial"),
            It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.OpenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task PortsChanged_TriggersAReload_PickingUpTheNewSet()
    {
        var (tree, _, watcher) = MakeTree([]);
        await LoadAndSettleAsync(tree);
        tree.RootNodes.Should().BeEmpty();

        watcher.CurrentPorts = [new DetectedComPort("COM7", "New Device (COM7)")];
        watcher.RaisePortsChanged();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tree.RootNodes.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
        }

        tree.RootNodes.Should().ContainSingle().Which.Profile.Host.Should().Be("COM7");
    }
}
