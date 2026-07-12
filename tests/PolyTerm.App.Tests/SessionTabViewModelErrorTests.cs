using Avalonia.Headless.XUnit;
using FluentAssertions;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the previously-missing <see cref="IProtocolSession.ErrorOccurred"/> subscription on
/// <see cref="SessionTabViewModel"/> — before this, a Faulted session only changed the tab title to
/// "✗" with no way to see why. <see cref="SessionTabViewModel.LastErrorMessage"/> now captures the
/// most recent error and is cleared again once the session reaches <see cref="SessionState.Connected"/>
/// (a fresh connect or a successful reconnect both mean whatever it described no longer applies).
/// </summary>
public sealed class SessionTabViewModelErrorTests
{
    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Emit(byte[] data) => OutputReceived?.Invoke(this, data);
    }

    private sealed class FakeProtocolSession : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = new() { Name = "test-session", ProtocolId = "ssh" };
        public SessionState State { get; private set; } = SessionState.Connected;

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal { get; } = new FakeTerminalChannel();
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseError(Exception ex, bool isFatal = true) =>
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = isFatal });

        public void RaiseStateChange(SessionState next)
        {
            var prev = State;
            State = next;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
        }
    }

    // OnErrorOccurred/OnStateChanged both dispatch via Avalonia.Threading.Dispatcher.UIThread.Post —
    // pump it so the assertions below see the update rather than racing it.
    private static async Task PumpDispatcher() =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });

    [AvaloniaFact]
    public async Task ErrorOccurred_SetsLastErrorMessage()
    {
        var session = new FakeProtocolSession();
        using var tab = new SessionTabViewModel(session);

        session.RaiseError(new InvalidOperationException("RDP fatal error 3."));
        await PumpDispatcher();

        tab.LastErrorMessage.Should().Be("RDP fatal error 3.");
    }

    [AvaloniaFact]
    public async Task LastErrorMessage_IsClearedOnceSessionReachesConnected()
    {
        var session = new FakeProtocolSession();
        using var tab = new SessionTabViewModel(session);
        session.RaiseError(new InvalidOperationException("transient failure"));
        await PumpDispatcher();
        tab.LastErrorMessage.Should().NotBeNull();

        session.RaiseStateChange(SessionState.Reconnecting);
        session.RaiseStateChange(SessionState.Connected);
        await PumpDispatcher();

        tab.LastErrorMessage.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task Dispose_UnsubscribesFromErrorOccurred_SoLaterErrorsAreIgnored()
    {
        var session = new FakeProtocolSession();
        var tab = new SessionTabViewModel(session);
        tab.Dispose();

        session.RaiseError(new InvalidOperationException("after dispose"));
        await PumpDispatcher();

        tab.LastErrorMessage.Should().BeNull();
    }
}
