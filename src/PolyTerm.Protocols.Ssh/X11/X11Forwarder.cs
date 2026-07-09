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
/// <b>Known simplification: no real xauth cookie substitution.</b> Real <c>ssh -X</c> sends a
/// randomly-generated "fake" cookie in the x11-req specifically so the real local xauth cookie is
/// never exposed to the remote host, then rewrites inbound X11 connections' authentication data from
/// the fake cookie back to the real one before handing them to the local X server. This class skips
/// that: it forwards the raw bytes of each inbound X11 connection to the local X server unchanged,
/// with no cookie awareness at all. This works against local X servers that don't enforce
/// authentication (e.g. VcXsrv/Xming run with "Disable access control", a common Windows dev setup)
/// but will be rejected by an X server that actually checks the cookie. Proper xauth read/substitution
/// is a real, deliberately deferred follow-up — see docs/todos.md's SSH X11 section.
/// </remarks>
/// <remarks>
/// <b>Not click-tested end to end.</b> This environment has no Linux/macOS sshd (Windows' own
/// Win32-OpenSSH sshd.exe does not implement X11 forwarding server-side at all — confirmed live: it
/// accepts <c>X11Forwarding yes</c> in sshd_config without error but "x11-req" is always refused), no
/// local X server, and no X11 client application — so the actual channel-open/data-relay logic below
/// is implemented against RFC 4254 §6.3.2 and confirmed-accessible SSH.NET internals, but has not been
/// exercised against a real inbound X11 channel. The channel-open detection and request-sending side
/// (<see cref="SshX11ShellChannel"/>) IS confirmed live against a real server.
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
    private readonly Dictionary<uint, Bridge> _bridges = [];
    private readonly object _bridgesLock = new();
    private bool _started;
    private bool _disposed;

    public X11Forwarder(Session session, ILogger logger, X11DisplayEndpoint display)
    {
        _session = session;
        _logger  = logger;
        _display = display;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
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
        try { bridge.Socket.Send(e.Message.Data, e.Message.Offset, e.Message.Size, SocketFlags.None); }
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
