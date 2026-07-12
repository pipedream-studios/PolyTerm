using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>
/// Shared <see cref="IProtocolSession"/> shape for Telnet/raw/rlogin — they differ only in how the
/// socket is connected (and, for rlogin, a pre-handshake) via <see cref="ConnectChannelAsync"/>.
/// </summary>
internal abstract class TcpSessionBase : IProtocolSession
{
    protected readonly SessionContext Ctx;
    private readonly ILogger _logger;
    private TcpSocketTerminalChannel? _channel;
    private SessionState _state = SessionState.Created;

    public Guid SessionId => Ctx.SessionId;
    public ConnectionProfile Profile => Ctx.Profile;
    public SessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    public ITerminalChannel? Terminal => _channel;
    public IFileTransferChannel? FileTransfer => null;
    public IFramebufferChannel? Framebuffer => null;
    public ITunnelManager? Tunnels => null;
    public INativeHostChannel? NativeHost => null;
    public IWebChannel? Web => null;

    protected TcpSessionBase(SessionContext ctx, ILogger logger)
    {
        Ctx     = ctx;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            _channel = await ConnectChannelAsync(ct).ConfigureAwait(false);
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connect failed for {Host}.", Ctx.Profile.Host);
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
    }

    /// <summary>Connects the socket (and any protocol-specific pre-handshake) and returns the ready-to-use terminal channel.</summary>
    protected abstract Task<TcpSocketTerminalChannel> ConnectChannelAsync(CancellationToken ct);

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closing);
        _channel?.Dispose();
        SetState(SessionState.Closed);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            await DisconnectAsync().ConfigureAwait(false);
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }
}
