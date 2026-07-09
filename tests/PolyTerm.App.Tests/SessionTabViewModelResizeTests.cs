using Avalonia.Headless.XUnit;
using FluentAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="SessionTabViewModel.LiveResizeUnsupported"/> — the M4 "RDP resize polish" tab
/// indicator shown when a native-host session's server doesn't support live Dynamic Resolution
/// Update (see <c>RdpNativeHostChannel.ResizeAsync</c>/docs/todos.md's RDP section). Driven against a
/// fake <see cref="INativeHostChannel"/> rather than a real MSTSC control — this is pure ViewModel
/// wiring, not the actual RDP resize behavior (which needs a live server, see docs/todos.md).
/// </summary>
public sealed class SessionTabViewModelResizeTests
{
    private sealed class FakeNativeHostChannel : INativeHostChannel
    {
        public nint Handle => 1;
        public bool? SupportsLiveResize { get; private set; }
        public event EventHandler? HandleChanged { add { } remove { } }
        public event EventHandler? SupportsLiveResizeChanged;

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public (byte[] Data, int Width, int Height)? CaptureSnapshot() => null;
        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void SetSupportsLiveResize(bool? value)
        {
            if (SupportsLiveResize == value) return;
            SupportsLiveResize = value;
            SupportsLiveResizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeGraphicalSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "test-rdp", ProtocolId = "rdp" };
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal => null;
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public FakeNativeHostChannel NativeHostFake { get; } = new();
        public INativeHostChannel? NativeHost => NativeHostFake;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task PumpDispatcher() =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

    [AvaloniaFact]
    public async Task LiveResizeUnsupported_IsFalse_UntilTheChannelReportsUnsupported()
    {
        var session = new FakeGraphicalSession();
        using var tab = new SessionTabViewModel(session);

        tab.LiveResizeUnsupported.Should().BeFalse();

        session.NativeHostFake.SetSupportsLiveResize(false);
        await PumpDispatcher();

        tab.LiveResizeUnsupported.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task LiveResizeUnsupported_GoesFalseAgain_IfTheChannelLaterReportsSupported()
    {
        var session = new FakeGraphicalSession();
        using var tab = new SessionTabViewModel(session);
        session.NativeHostFake.SetSupportsLiveResize(false);
        await PumpDispatcher();
        tab.LiveResizeUnsupported.Should().BeTrue();

        session.NativeHostFake.SetSupportsLiveResize(true);
        await PumpDispatcher();

        tab.LiveResizeUnsupported.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Dispose_UnsubscribesFromSupportsLiveResizeChanged_SoLaterChangesAreIgnored()
    {
        var session = new FakeGraphicalSession();
        var tab = new SessionTabViewModel(session);
        tab.Dispose();

        session.NativeHostFake.SetSupportsLiveResize(false);
        await PumpDispatcher();

        tab.LiveResizeUnsupported.Should().BeFalse();
    }
}
