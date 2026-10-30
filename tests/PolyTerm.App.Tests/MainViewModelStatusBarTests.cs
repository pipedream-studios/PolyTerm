using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the status bar's connection count and connection progress. Progress used to show inline in the
/// connection tree row (cut off unless the sidebar was dragged wide - Fluent's ProgressBar has a 200px
/// MinWidth) plus a separate banner for quick connect; every connect path now reports to
/// <see cref="MainViewModel.BeginConnecting"/> instead.
/// </summary>
public sealed class MainViewModelStatusBarTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning restore CS0067
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeProtocolSession(SessionState initial) : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "host", ProtocolId = "ssh" };
        public SessionState State { get; private set; } = initial;

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067

        public void MoveTo(SessionState next)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = previous, Current = next });
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal { get; } = new FakeTerminalChannel();
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MainViewModel MakeViewModel()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);
        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());
        return new MainViewModel(sessions.Object, tree);
    }

    [AvaloniaFact]
    public void ConnectingStatus_ShowsTheConnection_ThenTheExtraCount_AndClearsWhenAllFinish()
    {
        var vm = MakeViewModel();
        vm.HasConnectingStatus.Should().BeFalse();

        var tengen = vm.BeginConnecting("Connecting to Tengen");
        vm.ConnectingStatus.Should().Be("Connecting to Tengen…");

        var destiny = vm.BeginConnecting("Connecting to Destiny");
        vm.ConnectingStatus.Should().Be("Connecting to Tengen… (+1 more)");

        tengen.Dispose();
        vm.ConnectingStatus.Should().Be("Connecting to Destiny…");

        destiny.Dispose();
        destiny.Dispose(); // idempotent - must not remove anything else
        vm.HasConnectingStatus.Should().BeFalse();
        vm.ConnectingStatus.Should().BeNull();
    }

    [AvaloniaFact]
    public void ConnectingStatus_RaisesPropertyChanged_SoTheStatusBarUpdates()
    {
        var vm = MakeViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.BeginConnecting("Starting local shell").Dispose();

        changed.Should().Contain([nameof(MainViewModel.ConnectingStatus), nameof(MainViewModel.HasConnectingStatus)]);
    }

    [AvaloniaFact]
    public void ActiveConnections_CountsConnectedTabs_AndFollowsStateChanges()
    {
        var vm = MakeViewModel();
        vm.ActiveConnectionsText.Should().Be("No active connections");
        vm.HasActiveConnections.Should().BeFalse();

        var a = new FakeProtocolSession(SessionState.Connected);
        var b = new FakeProtocolSession(SessionState.Connected);
        vm.Tabs.Add(new SessionTabViewModel(a));
        vm.Tabs.Add(new SessionTabViewModel(b));
        vm.ActiveConnectionsText.Should().Be("2 active connections");

        b.MoveTo(SessionState.Reconnecting);
        Dispatcher.UIThread.RunJobs(); // SessionTabViewModel posts state changes to the UI thread
        vm.ActiveConnectionCount.Should().Be(1);
        vm.ActiveConnectionsText.Should().Be("1 active connection");

        vm.Tabs.RemoveAt(0);
        vm.ActiveConnectionsText.Should().Be("No active connections");
        vm.HasActiveConnections.Should().BeFalse();
    }
}
