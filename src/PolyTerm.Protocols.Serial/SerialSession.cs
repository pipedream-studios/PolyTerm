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
    private readonly object _stateLock = new();

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
    public IWebChannel? Web => null;

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
            var port = CreatePort(_ctx.Profile.Settings);
            port.Open();
            AssertControlLines(port);

            _channel = new SerialTerminalChannel(port);
            _channel.ReadFailed += OnReadFailed;
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

    /// <summary>An unopened port configured from a profile's settings; a missing or unrecognised line setting falls back to 9600 8N1 with no flow control.</summary>
    internal static SerialPort CreatePort(Dictionary<string, JsonElement> settings)
    {
        var portName = GetSetting(settings, "portName")
            ?? throw new InvalidOperationException("No serial port configured.");
        var baudRate = GetIntSetting(settings, "baudRate") ?? 9600;
        var dataBits = GetIntSetting(settings, "dataBits") ?? 8;
        var parity   = Enum.TryParse<Parity>(GetSetting(settings, "parity"), true, out var p) ? p : Parity.None;
        var stopBits = Enum.TryParse<StopBits>(GetSetting(settings, "stopBits"), true, out var sb) ? sb : StopBits.One;
        var handshake = Enum.TryParse<Handshake>(GetSetting(settings, "flowControl"), true, out var h) ? h : Handshake.None;

        return new SerialPort(portName, baudRate, parity, dataBits, stopBits) { Handshake = handshake };
    }

    /// <summary>
    /// Turns DTR and RTS on, as some devices stay silent until they are; .NET opens a port with both
    /// off (issue #342). This has to happen after <see cref="SerialPort.Open"/>: asked for beforehand,
    /// a driver without these lines (a pseudo-terminal, some virtual ports) fails the whole open.
    /// </summary>
    private void AssertControlLines(SerialPort port)
    {
        TryAssert("DTR", () => port.DtrEnable = true);

        // Under RTS/CTS flow control the driver owns RTS, and setting it throws.
        if (port.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
            TryAssert("RTS", () => port.RtsEnable = true);

        void TryAssert(string line, Action assert)
        {
            try
            {
                assert();
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Serial port {Port} can't set {Line}; carrying on without it.", port.PortName, line);
            }
        }
    }

    /// <summary>
    /// Closes the port. Safe to call more than once and alongside <see cref="OnReadFailed"/> (the user
    /// closing the tab as the adapter is pulled): teardown runs once, and a session that already
    /// faulted stays <see cref="SessionState.Faulted"/>.
    /// </summary>
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_state is SessionState.Closed or SessionState.Faulted) return Task.CompletedTask;
            SetState(SessionState.Closing);
            _channel?.Dispose();
            SetState(SessionState.Closed);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The port failed under a live session, most often because the USB adapter was unplugged. Without
    /// this the session stayed Connected over a dead port and the tab never showed it (issue #91). Ends
    /// in <see cref="SessionState.Faulted"/> with the error raised, as a lost connection rather than a
    /// normal close, so the tab shows it failed and why.
    /// </summary>
    private void OnReadFailed(object? sender, Exception ex)
    {
        var portName = GetSetting(_ctx.Profile.Settings, "portName");
        lock (_stateLock)
        {
            if (_state != SessionState.Connected) return;
            _logger.LogWarning(ex, "Serial port {Port} failed; closing the session.", portName);
            _channel?.Dispose();
            SetState(SessionState.Faulted);
        }
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs
        {
            Exception = new IOException($"Lost serial port {portName}: {ex.Message}", ex),
            IsFatal = true,
        });
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
