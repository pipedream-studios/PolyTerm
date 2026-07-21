using System.Text.Json;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the "On Close" setting (Ask/Close/Detach) and same-process session reattach added in issue
/// #77 Phase 1 — previously closing a tab always called <c>DisconnectAsync</c> unconditionally, with no
/// way to keep a session running in the background and reopen it later.
/// </summary>
public sealed class MainViewModelCloseAndReattachTests
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

    private sealed class FakeProtocolSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public required ConnectionProfile Profile { get; init; }
        public SessionState State { get; private set; } = SessionState.Connected;
        public bool DisconnectCalled { get; private set; }

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            DisconnectCalled = true;
            return Task.CompletedTask;
        }

        // A real (fake) Terminal, not null - MainViewModel.OnSessionOpened discards any session with
        // every capability null (nothing to render a tab for at all), which would otherwise make this
        // fake indistinguishable from that "surfaceless" case and get silently dropped before a tab is
        // ever created.
        public ITerminalChannel? Terminal { get; } = new FakeTerminalChannel();
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseStateChange(SessionState next)
        {
            var prev = State;
            State = next;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
        }
    }

    private static ConnectionProfile MakeProfile(string? onClose = null)
    {
        var settings = new Dictionary<string, JsonElement>();
        if (onClose is not null)
            settings["onClose"] = JsonSerializer.SerializeToElement(onClose);
        return new ConnectionProfile { Name = "test-session", ProtocolId = "ssh", Settings = settings };
    }

    private static (MainViewModel Vm, Mock<ISessionManager> Sessions) MakeViewModel()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        var sessions = new Mock<ISessionManager>();
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, sessions.Object, registry.Object, new FakeComPortWatcher());
        var vm = new MainViewModel(sessions.Object, tree);
        return (vm, sessions);
    }

    /// <summary>
    /// The real <see cref="ISessionManager.CloseAsync"/> looks up the session by id and calls
    /// <see cref="IProtocolSession.DisconnectAsync"/> on it - the mock knows nothing about
    /// <paramref name="session"/> unless told to route through to it, which is what
    /// <see cref="MainViewModel"/>'s "Close" branch relies on actually happening (it calls
    /// <c>_sessions.CloseAsync</c> directly, not the tab's own disconnect path - see that method's own
    /// comment on why).
    /// </summary>
    private static void RouteCloseToDisconnect(Mock<ISessionManager> sessions, FakeProtocolSession session) =>
        sessions.Setup(s => s.CloseAsync(session.SessionId, It.IsAny<CancellationToken>()))
            .Returns(() => session.DisconnectAsync());

    [AvaloniaFact]
    public async Task CloseTabAsync_WithOnCloseSetToClose_DisconnectsTheSession()
    {
        var (vm, sessions) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Close") };
        RouteCloseToDisconnect(sessions, session);
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        session.DisconnectCalled.Should().BeTrue("onClose=Close should behave exactly like before #77 - always disconnect");
    }

    [AvaloniaFact]
    public async Task CloseTabAsync_WithOnCloseSetToDetach_DoesNotDisconnect_AndListsTheSessionAsDetached()
    {
        var (vm, _) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Detach") };
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        session.DisconnectCalled.Should().BeFalse("Detach must not touch the underlying session at all");
        vm.Tabs.Should().NotContain(tab);
        vm.DetachedSessions.Should().ContainSingle(d => d.SessionId == session.SessionId);
    }

    [AvaloniaFact]
    public async Task CloseTabAsync_WithOnCloseSetToAsk_AndUserChoosesDetach_DetachesWithoutDisconnecting()
    {
        var (vm, _) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Ask") };
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);
        vm.ConfirmCloseAsync = _ => Task.FromResult(CloseSessionChoice.Detach);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        session.DisconnectCalled.Should().BeFalse();
        vm.DetachedSessions.Should().ContainSingle(d => d.SessionId == session.SessionId);
    }

    [AvaloniaFact]
    public async Task CloseTabAsync_WithOnCloseSetToAsk_AndUserChoosesClose_Disconnects()
    {
        var (vm, sessions) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Ask") };
        RouteCloseToDisconnect(sessions, session);
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);
        vm.ConfirmCloseAsync = _ => Task.FromResult(CloseSessionChoice.Close);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        session.DisconnectCalled.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task CloseTabAsync_WithOnCloseSetToAsk_AndUserCancels_LeavesTheTabOpen()
    {
        var (vm, _) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Ask") };
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);
        vm.ConfirmCloseAsync = _ => Task.FromResult(CloseSessionChoice.Cancel);

        await vm.CloseTabCommand.ExecuteAsync(tab);

        session.DisconnectCalled.Should().BeFalse();
        vm.Tabs.Should().Contain(tab, "Cancel should leave everything exactly as it was");
        vm.DetachedSessions.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task CloseTabAsync_DefaultsToAskWhenNoOnCloseSettingIsStored()
    {
        var (vm, _) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile(onClose: null) };
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);
        var askedFor = "";
        vm.ConfirmCloseAsync = name => { askedFor = name; return Task.FromResult(CloseSessionChoice.Detach); };

        await vm.CloseTabCommand.ExecuteAsync(tab);

        askedFor.Should().NotBeEmpty("Ask should be the default when a connection has never set an explicit onClose value");
    }

    /// <summary>
    /// The tab's own "✕" button binds to <c>SessionTabViewModel.CloseCommand</c>, not
    /// <c>MainViewModel.CloseTabCommand</c> directly - this confirms that path also goes through the
    /// On-Close decision via <see cref="SessionTabViewModel.OnCloseRequested"/>, not straight to
    /// <c>DisconnectAsync</c> as it did before #77. A real gap caught during implementation: it's easy
    /// to wire the keyboard-shortcut path correctly and miss the button's own binding.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTabsOwnCloseButton_AlsoRoutesThroughTheOnCloseDecision_NotDirectlyToDisconnect()
    {
        var (vm, sessions) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Detach") };
        // Raise the real SessionOpened event (not a direct Tabs.Add) so the tab gets constructed the
        // exact way MainViewModel.OnSessionOpened does it in the real app - including wiring
        // OnCloseRequested, which is exactly the thing this test needs to exercise.
        sessions.Raise(s => s.SessionOpened += null, sessions.Object, new SessionEventArgs { Session = session });
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }); // OnSessionOpened dispatches onto the UI thread
        var tab = vm.Tabs.Single();

        await tab.CloseCommand.ExecuteAsync(null);

        session.DisconnectCalled.Should().BeFalse("Detach via the tab's own close button should behave identically to Detach via the keyboard shortcut / Session menu");
        vm.DetachedSessions.Should().ContainSingle(d => d.SessionId == session.SessionId);
    }

    [AvaloniaFact]
    public async Task ReattachAsync_ReopensATabAroundTheSameLiveSessionInstance_NotANewOne()
    {
        var (vm, sessions) = MakeViewModel();
        var session = new FakeProtocolSession { Profile = MakeProfile("Detach") };
        sessions.Setup(s => s.ActiveSessions).Returns([session]);
        var tab = new SessionTabViewModel(session);
        vm.Tabs.Add(tab);
        await vm.CloseTabCommand.ExecuteAsync(tab); // detach it first
        vm.DetachedSessions.Should().ContainSingle();

        vm.PickDetachedSessionAsync = items => Task.FromResult<DetachedSessionItem?>(items[0]);
        await vm.ReattachCommand.ExecuteAsync(null);

        vm.DetachedSessions.Should().BeEmpty("the session is no longer detached once reattached");
        vm.Tabs.Should().ContainSingle(t => t.SessionId == session.SessionId);
        vm.ActiveTab.Should().NotBeNull();
        vm.ActiveTab!.SessionId.Should().Be(session.SessionId);
    }

    [AvaloniaFact]
    public async Task ReattachAsync_WithNoDetachedSessions_IsANoOp()
    {
        var (vm, _) = MakeViewModel();
        var pickerCalled = false;
        vm.PickDetachedSessionAsync = items => { pickerCalled = true; return Task.FromResult<DetachedSessionItem?>(null); };

        await vm.ReattachCommand.ExecuteAsync(null);

        pickerCalled.Should().BeFalse("nothing to pick from - the picker shouldn't even open");
    }
}
