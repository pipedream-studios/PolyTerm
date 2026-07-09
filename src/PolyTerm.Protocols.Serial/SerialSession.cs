using System.IO.Ports;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>A serial/COM port session — opens a <see cref="SerialPort"/> and wraps it in a <see cref="SerialTerminalChannel"/>.</summary>
public sealed class SerialSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly ILogger<SerialSession> _logger;
    private SerialTerminalChannel? _channel;
    private SessionState _state = SessionState.Created;

    public Guid SessionId => _ctx.SessionId;
    public ConnectionProfile Profile => _ctx.Profile;
    public SessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    public ITerminalChannel? Terminal => _channel;
    public IFileTransferChannel? FileTransfer => null;
    public IFramebufferChannel? Framebuffer => null;
    public ITunnelManager? Tunnels => null;
    public INativeHostChannel? NativeHost => null;

    public SerialSession(SessionContext ctx, ILogger<SerialSession> logger)
    {
        _ctx    = ctx;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            var settings = _ctx.Profile.Settings;
            var portName = GetSetting(settings, "portName")
                ?? throw new InvalidOperationException("No serial port configured.");
            var baudRate = GetIntSetting(settings, "baudRate") ?? 9600;
            var dataBits = GetIntSetting(settings, "dataBits") ?? 8;
            var parity   = Enum.TryParse<Parity>(GetSetting(settings, "parity"), true, out var p) ? p : Parity.None;
            var stopBits = Enum.TryParse<StopBits>(GetSetting(settings, "stopBits"), true, out var sb) ? sb : StopBits.One;

            var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            port.Open();

            _channel = new SerialTerminalChannel(port);
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial connect failed for {Port}.", GetSetting(_ctx.Profile.Settings, "portName"));
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
        return Task.CompletedTask;
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

    private static string? GetSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetIntSetting(Dictionary<string, JsonElement> s, string key) =>
        s.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
