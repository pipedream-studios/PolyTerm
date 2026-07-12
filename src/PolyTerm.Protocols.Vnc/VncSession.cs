using System.Text;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Vnc;

/// <summary>A VNC (RFB) session — connects via <see cref="MarcusW.VncClient"/> and exposes the desktop through <see cref="VncFramebufferChannel"/>.</summary>
public sealed class VncSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VncSession> _logger;
    private VncFramebufferChannel? _channel;
    private RfbConnection? _connection;
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

    public VncSession(SessionContext ctx, ILoggerFactory loggerFactory)
    {
        _ctx            = ctx;
        _loggerFactory  = loggerFactory;
        _logger         = loggerFactory.CreateLogger<VncSession>();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            var host     = _ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
            var port     = _ctx.Profile.Port is > 0 ? _ctx.Profile.Port.Value : 5900;
            var password = _ctx.Credential?.Secret is { Length: > 0 } secret ? Encoding.UTF8.GetString(secret) : "";

            var channel = new VncFramebufferChannel();
            var client  = new VncClient(_loggerFactory);

            var parameters = new ConnectParameters
            {
                TransportParameters   = new TcpTransportParameters { Host = host, Port = port },
                AuthenticationHandler = new PasswordAuthenticationHandler(password),
                InitialRenderTarget   = channel,
                AllowSharedConnection = true,
            };

            var connection = await client.ConnectAsync(parameters, ct).ConfigureAwait(false);
            channel.Connection = connection;

            _channel    = channel;
            _connection = connection;
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VNC connect failed for {Host}.", _ctx.Profile.Host);
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closing);
        if (_connection is not null)
            await _connection.CloseAsync().ConfigureAwait(false);
        SetState(SessionState.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            await DisconnectAsync().ConfigureAwait(false);
        _connection?.Dispose();
        _channel?.Dispose();
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }
}
