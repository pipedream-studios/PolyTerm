using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// An RDP session on Linux/macOS, connecting via the native FreeRDP shim rather than Windows' MSTSC
/// ActiveX control (see <c>RdpSession</c> in <c>PolyTerm.Protocols.Rdp</c> for that path — this is a
/// deliberately separate implementation, not a shared one, since the two have essentially nothing in
/// common beyond the protocol name). Exposes the desktop through <see cref="IFramebufferChannel"/>
/// (<see cref="SurfaceKind.Framebuffer"/>), the same rendering model VNC already uses.
/// </summary>
internal sealed class RdpFreerdpSession : IProtocolSession
{
    private const int DefaultWidth = 1024;
    private const int DefaultHeight = 768;

    private readonly SessionContext _ctx;
    private readonly ILogger<RdpFreerdpSession> _logger;
    private RdpFreerdpFramebufferChannel? _channel;
    private SessionState _state = SessionState.Created;

    public Guid SessionId => _ctx.SessionId;
    public ConnectionProfile Profile => _ctx.Profile;
    public SessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    public ITerminalChannel? Terminal => null;
    public IFileTransferChannel? FileTransfer => null;
    public IFramebufferChannel? Framebuffer => _channel;
    public ITunnelManager? Tunnels => null;
    public INativeHostChannel? NativeHost => null;
    public IWebChannel? Web => null;

    public RdpFreerdpSession(SessionContext ctx, ILoggerFactory loggerFactory)
    {
        _ctx = ctx;
        _logger = loggerFactory.CreateLogger<RdpFreerdpSession>();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            var host = _ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
            var port = _ctx.Profile.Port is > 0 ? _ctx.Profile.Port.Value : 3389;
            var username = _ctx.Credential?.Username ?? "";
            var password = _ctx.Credential?.Secret is { Length: > 0 } secret
                ? System.Text.Encoding.UTF8.GetString(secret)
                : "";
            var domain = _ctx.Profile.Settings.TryGetValue("domain", out var domainEl) ? domainEl.GetString() : null;

            var channel = new RdpFreerdpFramebufferChannel();
            await channel.ConnectAsync(host, port, username, password, domain, DefaultWidth, DefaultHeight, ct)
                .ConfigureAwait(false);

            _channel = channel;
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RDP (FreeRDP) connect failed for {Host}.", _ctx.Profile.Host);
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
    }

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
