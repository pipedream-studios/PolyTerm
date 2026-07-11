using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Messages.Connection;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Bridges inbound "x11" SSH channels (opened by the server whenever a remote X client — e.g. an
/// app the user ran on the SSH host — connects to the forwarded <c>DISPLAY</c>) to the local X server.
/// Built on <see cref="Session"/>'s own public per-message-type events
/// (<c>ChannelOpenReceived</c>/<c>ChannelDataReceived</c>/<c>ChannelWindowAdjustReceived</c>/
/// <c>ChannelEofReceived</c>/<c>ChannelCloseReceived</c> — all confirmed public via reflection, unlike
/// the internal <c>Channels.*</c> classes <see cref="SshX11ShellChannel"/> needs reflection for) rather
/// than a channel-type class, since <c>ChannelForwardedTcpip</c> (the closest existing SSH.NET
/// equivalent) only binds to an <see cref="System.Net.IPEndPoint"/> — no Unix-domain-socket support,
/// which rules it out for the common Linux/macOS case where the local X server is Unix-socket-only.
/// </summary>
/// <remarks>
/// <b>Real xauth cookie substitution.</b> <see cref="SshSession"/> sends a randomly-generated "fake"
/// cookie in the x11-req specifically so the real local xauth cookie is never exposed to the remote
/// host. The first data packet of each inbound X11 connection (its ConnClientPrefix — see
/// <see cref="X11ConnectionSetupRewriter"/>) is inspected and, if its authorization data matches that
/// fake cookie, rewritten to the real local cookie (read once at construction via
/// <see cref="XauthCookieStore"/>) before being forwarded to the local X server — the same thing real
/// <c>ssh -X</c> does, and the whole reason a fake cookie is generated at all. If no real cookie can be
/// found (many Windows X servers don't use xauth at all, e.g. VcXsrv run with "Disable access
/// control"), every packet — including the first — is forwarded unchanged, matching this class's
/// previous behavior exactly; that's a legitimate, common setup, not treated as an error.
/// </remarks>
/// <remarks>
/// <b>Verified live</b> (see <c>SshX11InboundBridgeTests</c>) against a real Linux sshd with X11
/// forwarding enabled and a real local X server: a real <c>xclock</c> run on the remote host completes
/// a real inbound X11 channel-open, gets bridged to the local X server, and shows up as a genuinely
/// connected client. This surfaced one real bug, now fixed: <see cref="Start"/> must call
/// <c>Session.RegisterMessage("SSH_MSG_CHANNEL_OPEN")</c> before the first inbound channel-open, or
/// SSH.NET's message factory throws ("Message type 90 is not valid in the current context") and takes
/// down the whole SSH session, not just the X11 channel.
/// </remarks>
internal sealed class X11Forwarder : IDisposable
{
    // Effectively unbounded — matches SSH.NET's own InitialLocalWindowSize (confirmed via reflection:
    // 2147483647), so this side of the flow control practically never needs replenishing.
    private const uint LocalWindowSize = int.MaxValue;
    private const uint LocalPacketSize = 65536;

    private readonly Session _session;
    private readonly ILogger _logger;
    private readonly X11DisplayEndpoint _display;
    private readonly byte[] _fakeCookie;
    private readonly byte[]? _realCookie;
    private readonly Dictionary<uint, Bridge> _bridges = [];
    private readonly object _bridgesLock = new();
    private bool _started;
    private bool _disposed;

    /// <param name="fakeCookie">The same disposable cookie <see cref="SshSession"/> sent to the remote host in its x11-req — needed here to recognize it in each inbound connection's first packet.</param>
    public X11Forwarder(Session session, ILogger logger, X11DisplayEndpoint display, byte[] fakeCookie)
    {
        _session    = session;
        _logger     = logger;
        _display    = display;
        _fakeCookie = fakeCookie;
        _realCookie = XauthCookieStore.TryReadCookie(display.DisplayNumber);
        if (_realCookie is null)
            _logger.LogDebug("No local Xauthority cookie found for display {Display} — inbound X11 connections will be forwarded without cookie substitution.", display.DisplayNumber);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        // SSH.NET's SshMessageFactory gates message construction per type ("enabled" or not) - Session's
        // own Connect()/ConnectAsync() registers CHANNEL_OPEN_CONFIRMATION/FAILURE/DATA/etc. after auth,
        // but deliberately not SSH_MSG_CHANNEL_OPEN itself, since the library's default assumption is
        // that only the client ever opens channels. X11 forwarding needs the reverse (the server opens
        // an "x11" channel back to us whenever a remote X client connects) - without this, the very
        // first inbound channel-open crashes Session's own message listener with "Message type 90 is not
        // valid in the current context" (confirmed live: this took down the whole SSH session, not just
        // the X11 channel, triggering PolyTerm's own auto-reconnect). RegisterMessage is a genuinely
        // public Session method - no reflection needed, unlike everything in SshNetInternals.
        _session.RegisterMessage("SSH_MSG_CHANNEL_OPEN");

        _session.ChannelOpenReceived         += OnChannelOpenReceived;
        _session.ChannelDataReceived         += OnChannelDataReceived;
        _session.ChannelWindowAdjustReceived += OnChannelWindowAdjustReceived;
        _session.ChannelEofReceived          += OnChannelEofReceived;
        _session.ChannelCloseReceived        += OnChannelCloseReceived;
    }

    private void OnChannelOpenReceived(object? sender, MessageEventArgs<ChannelOpenMessage> e)
    {
        var msg = e.Message;
        if (System.Text.Encoding.ASCII.GetString(msg.ChannelType) != "x11") return; // not ours — leave it alone

        Socket socket;
        try
        {
            socket = new Socket(_display.Endpoint.AddressFamily, SocketType.Stream, ProtocolType.IP);
            socket.Connect(_display.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "X11 channel-open rejected: couldn't reach local X server at {Endpoint}.", _display.Endpoint);
            // RFC 4254 §5.1 SSH_OPEN_CONNECT_FAILED — the reason-code constants on
            // ChannelOpenFailureMessage are internal (confirmed via reflection), but these are fixed
            // protocol values, not SSH.NET implementation details, so hardcoding is fine.
            SshNetInternals.SendMessage(_session, new ChannelOpenFailureMessage(
                msg.LocalChannelNumber, "Local X server unreachable.", 2u));
            return;
        }

        var ourChannelNumber = SshNetInternals.NextChannelNumber(_session);
        var bridge = new Bridge(socket, msg.LocalChannelNumber, msg.InitialWindowSize);
        lock (_bridgesLock) _bridges[ourChannelNumber] = bridge;

        SshNetInternals.SendMessage(_session, new ChannelOpenConfirmationMessage(
            msg.LocalChannelNumber, LocalWindowSize, LocalPacketSize, ourChannelNumber));

        _ = Task.Run(() => PumpSocketToChannelAsync(ourChannelNumber, bridge));
    }

    /// <summary>Reads from the local X server socket and relays each chunk to the server as channel data, respecting the remote's advertised window.</summary>
    private async Task PumpSocketToChannelAsync(uint ourChannelNumber, Bridge bridge)
    {
        var buffer = new byte[LocalPacketSize];
        try
        {
            while (true)
            {
                var read = await bridge.Socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (read == 0) break;

                await bridge.WaitForRemoteWindowAsync(read).ConfigureAwait(false);
                SshNetInternals.SendMessage(_session, new ChannelDataMessage(bridge.RemoteChannelNumber, buffer[..read]));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "X11 channel {Channel}: local socket read loop ended.", ourChannelNumber);
        }
        finally
        {
            CloseBridge(ourChannelNumber, sendEof: true);
        }
    }

    private void OnChannelDataReceived(object? sender, MessageEventArgs<ChannelDataMessage> e)
    {
        if (!TryGetBridge(e.Message.LocalChannelNumber, out var bridge)) return;
        try
        {
            // Only the very first packet of a connection can be the X11 ConnClientPrefix carrying the
            // auth cookie — every packet after that is ordinary X protocol traffic PolyTerm has no
            // business inspecting or modifying.
            if (!bridge.FirstPacketSeen)
            {
                bridge.FirstPacketSeen = true;
                var firstPacket = new byte[e.Message.Size];
                Array.Copy(e.Message.Data, e.Message.Offset, firstPacket, 0, e.Message.Size);
                var toSend = X11ConnectionSetupRewriter.TryRewriteCookie(firstPacket, _fakeCookie, _realCookie);
                bridge.Socket.Send(toSend, 0, toSend.Length, SocketFlags.None);
            }
            else
            {
                bridge.Socket.Send(e.Message.Data, e.Message.Offset, e.Message.Size, SocketFlags.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "X11 channel {Channel}: local socket write failed.", e.Message.LocalChannelNumber);
            CloseBridge(e.Message.LocalChannelNumber, sendEof: false);
        }
    }

    private void OnChannelWindowAdjustReceived(object? sender, MessageEventArgs<ChannelWindowAdjustMessage> e)
    {
        if (TryGetBridge(e.Message.LocalChannelNumber, out var bridge))
            bridge.AddRemoteWindow(e.Message.BytesToAdd);
    }

    private void OnChannelEofReceived(object? sender, MessageEventArgs<ChannelEofMessage> e) =>
        CloseBridge(e.Message.LocalChannelNumber, sendEof: false);

    private void OnChannelCloseReceived(object? sender, MessageEventArgs<ChannelCloseMessage> e) =>
        CloseBridge(e.Message.LocalChannelNumber, sendEof: false);

    private bool TryGetBridge(uint ourChannelNumber, out Bridge bridge)
    {
        lock (_bridgesLock) return _bridges.TryGetValue(ourChannelNumber, out bridge!);
    }

    private void CloseBridge(uint ourChannelNumber, bool sendEof)
    {
        Bridge? bridge;
        lock (_bridgesLock)
        {
            if (!_bridges.Remove(ourChannelNumber, out bridge)) return;
        }

        if (sendEof)
        {
            try { SshNetInternals.SendMessage(_session, new ChannelEofMessage(bridge.RemoteChannelNumber)); }
            catch { /* session may already be gone */ }
            try { SshNetInternals.SendMessage(_session, new ChannelCloseMessage(bridge.RemoteChannelNumber)); }
            catch { /* session may already be gone */ }
        }
        bridge.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            _session.ChannelOpenReceived         -= OnChannelOpenReceived;
            _session.ChannelDataReceived         -= OnChannelDataReceived;
            _session.ChannelWindowAdjustReceived -= OnChannelWindowAdjustReceived;
            _session.ChannelEofReceived          -= OnChannelEofReceived;
            _session.ChannelCloseReceived        -= OnChannelCloseReceived;
            try { _session.UnRegisterMessage("SSH_MSG_CHANNEL_OPEN"); } catch { /* session may already be gone */ }
        }
        List<uint> channels;
        lock (_bridgesLock) channels = [.. _bridges.Keys];
        foreach (var ch in channels)
            CloseBridge(ch, sendEof: false);
    }

    /// <summary>
    /// Tracks the remote side's advertised receive window for one X11 channel — how many more bytes
    /// we're allowed to send before we must wait for a <c>ChannelWindowAdjustMessage</c>.
    /// <see cref="SemaphoreSlim"/> only supports waiting for exactly one permit per call, not "wait
    /// until N are available" (needed here since we send variable-sized chunks), so this tracks the
    /// remaining window as a plain counter and uses the semaphore only as a "something changed, go
    /// re-check the counter" signal.
    /// </summary>
    private sealed class Bridge(Socket socket, uint remoteChannelNumber, uint initialRemoteWindow) : IDisposable
    {
        private readonly object _windowLock = new();
        private readonly SemaphoreSlim _windowChanged = new(0);
        private long _remoteWindowRemaining = initialRemoteWindow;

        public Socket Socket { get; } = socket;
        public uint RemoteChannelNumber { get; } = remoteChannelNumber;

        /// <summary>Set true after this bridge's first data packet has been seen — only that one can be the ConnClientPrefix carrying the auth cookie (see <see cref="OnChannelDataReceived"/>).</summary>
        public bool FirstPacketSeen { get; set; }

        public void AddRemoteWindow(uint bytesToAdd)
        {
            lock (_windowLock) _remoteWindowRemaining += bytesToAdd;
            _windowChanged.Release();
        }

        public async Task WaitForRemoteWindowAsync(int needed)
        {
            while (true)
            {
                lock (_windowLock)
                {
                    if (_remoteWindowRemaining >= needed)
                    {
                        _remoteWindowRemaining -= needed;
                        return;
                    }
                }
                await _windowChanged.WaitAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Socket.Dispose();
            _windowChanged.Dispose();
        }
    }
}
