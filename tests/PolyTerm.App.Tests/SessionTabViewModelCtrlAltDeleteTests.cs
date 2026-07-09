using FluentAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="SessionTabViewModel.CanSendCtrlAltDelete"/>/<see cref="SessionTabViewModel.SendCtrlAltDeleteAsync"/>'s
/// capability-scoped dispatch logic against fake <see cref="IFramebufferChannel"/>/<see cref="INativeHostChannel"/>
/// implementations — the actual wire-level VNC key sequence and MSTSC <c>SasSequence</c> call are real,
/// protocol-specific code covered separately (<c>VncFramebufferChannel.SendCtrlAltDeleteAsync</c>,
/// <c>RdpNativeHostChannel.SendCtrlAltDeleteAsync</c>); this is pure ViewModel wiring — does the right
/// channel get called, is the capability flag correct for each session shape.
/// </summary>
public sealed class SessionTabViewModelCtrlAltDeleteTests
{
    private sealed class FakeFramebufferChannel : IFramebufferChannel
    {
        public int CtrlAltDeleteCallCount { get; private set; }
        public FramebufferFormat Format => FramebufferFormat.Rgba32;
        public int Width => 1;
        public int Height => 1;
#pragma warning disable CS0067
        public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;
        public event EventHandler<ClipboardPayload>? ClipboardReceived;
#pragma warning restore CS0067
        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;
        public byte[]? CaptureSnapshot() => null;
        public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default)
        {
            CtrlAltDeleteCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeNativeHostChannel : INativeHostChannel
    {
        public int CtrlAltDeleteCallCount { get; private set; }
        public nint Handle => 1;
        public bool? SupportsLiveResize => null;
        public event EventHandler? HandleChanged { add { } remove { } }
        public event EventHandler? SupportsLiveResizeChanged { add { } remove { } }
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public (byte[] Data, int Width, int Height)? CaptureSnapshot() => null;
        public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default)
        {
            CtrlAltDeleteCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "test", ProtocolId = "test" };
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal { get; init; }
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer { get; init; }
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost { get; init; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void CanSendCtrlAltDelete_IsFalse_ForATerminalOnlySession()
    {
        using var tab = new SessionTabViewModel(new FakeSession());
        tab.CanSendCtrlAltDelete.Should().BeFalse("a plain SSH/local-shell session has no graphical desktop to send it to");
    }

    [Fact]
    public void CanSendCtrlAltDelete_IsTrue_ForAFramebufferSession()
    {
        using var tab = new SessionTabViewModel(new FakeSession { Framebuffer = new FakeFramebufferChannel() });
        tab.CanSendCtrlAltDelete.Should().BeTrue();
    }

    [Fact]
    public void CanSendCtrlAltDelete_IsTrue_ForANativeHostSession()
    {
        using var tab = new SessionTabViewModel(new FakeSession { NativeHost = new FakeNativeHostChannel() });
        tab.CanSendCtrlAltDelete.Should().BeTrue();
    }

    [Fact]
    public async Task SendCtrlAltDeleteAsync_CallsTheFramebufferChannel_WhenPresent()
    {
        var framebuffer = new FakeFramebufferChannel();
        using var tab = new SessionTabViewModel(new FakeSession { Framebuffer = framebuffer });

        await tab.SendCtrlAltDeleteAsync();

        framebuffer.CtrlAltDeleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendCtrlAltDeleteAsync_CallsTheNativeHostChannel_WhenPresent()
    {
        var nativeHost = new FakeNativeHostChannel();
        using var tab = new SessionTabViewModel(new FakeSession { NativeHost = nativeHost });

        await tab.SendCtrlAltDeleteAsync();

        nativeHost.CtrlAltDeleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendCtrlAltDeleteAsync_IsANoOp_WhenNeitherChannelIsPresent()
    {
        using var tab = new SessionTabViewModel(new FakeSession());

        var act = async () => await tab.SendCtrlAltDeleteAsync();

        await act.Should().NotThrowAsync();
    }
}
