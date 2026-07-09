using System.Text;
using Renci.SshNet;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Terminal channel for X11-forwarding-enabled sessions — built directly on a reflected SSH.NET
/// channel (<see cref="SshNetInternals"/>) instead of <see cref="ShellStream"/>, since X11 forwarding
/// needs "x11-req" sent before "shell" for <c>DISPLAY</c> to be auto-exported into the remote shell's
/// environment, and <c>ShellStream</c>'s constructor sends pty-req/shell-req atomically with no hook
/// in between. Used only when a profile has X11 forwarding enabled — every other SSH session keeps
/// using the well-tested <see cref="SshTerminalChannel"/>/<see cref="ShellStream"/> path unchanged.
/// </summary>
internal sealed class SshX11ShellChannel : ITerminalChannel, IDisposable
{
    private readonly object _channel;
    private readonly string? _loginBanner;
    private readonly Action _unsubscribeData;
    private readonly Action _unsubscribeClosed;
    private readonly object _bufferLock = new();
    private readonly List<byte[]> _earlyBuffer = [];
    private bool _liveMode;
    private bool _disposed;

    private Action<ReadOnlyMemory<byte>>? _sinks;
    private EventHandler<ReadOnlyMemory<byte>>? _outputReceived;

    public string TerminalType { get; }

    /// <summary>
    /// Unlike <see cref="SshTerminalChannel"/>'s lazily-started pull read loop, this channel's data
    /// arrives push-style from SSH.NET's own message-listener thread the moment the channel is
    /// created — before pty-req/x11-req/shell-req are even sent, let alone before any App-side
    /// subscriber exists. Everything received before the <b>first ever</b> subscription is buffered
    /// (see <see cref="_earlyBuffer"/>) and replayed here, after the login banner, then this switches
    /// to live pass-through — matching <see cref="SshTerminalChannel"/>'s "no bytes lost while the
    /// terminal surface is still wiring up" guarantee despite the different (push vs. pull) transport.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add
        {
            byte[][] buffered;
            lock (_bufferLock)
            {
                buffered = [.. _earlyBuffer];
                _earlyBuffer.Clear();
                _liveMode = true;
            }

            _outputReceived += value;
            if (_loginBanner is not null)
                value?.Invoke(this, NormalizeBannerBytes(_loginBanner));
            foreach (var chunk in buffered)
                value?.Invoke(this, chunk);
        }
        remove => _outputReceived -= value;
    }

    public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sinks += sink;

    /// <summary>
    /// Creates the channel, subscribes to its data immediately (see <see cref="OutputReceived"/>'s
    /// doc comment), then sends pty-req (+ optional x11-req) + shell-req. Throws
    /// <see cref="Common.SshException"/> if the server rejects any of them (same failure shape
    /// <see cref="SshSession.ConnectCoreAsync"/> already expects from the <c>ShellStream</c> path).
    /// </summary>
    public SshX11ShellChannel(Session session, string terminalType, uint columns, uint rows,
        X11ForwardingParameters? x11, string? loginBanner = null)
    {
        TerminalType = terminalType;
        _loginBanner = string.IsNullOrWhiteSpace(loginBanner) ? null : loginBanner;
        _channel = SshNetInternals.CreateAndOpenChannel(session);

        _unsubscribeData = SshNetInternals.SubscribeDataReceived(_channel, segment =>
        {
            var chunk = new ReadOnlyMemory<byte>(segment.Array, segment.Offset, segment.Count);
            lock (_bufferLock)
            {
                if (!_liveMode)
                {
                    _earlyBuffer.Add(chunk.ToArray());
                    return;
                }
            }
            _outputReceived?.Invoke(this, chunk);
            _sinks?.Invoke(chunk);
        });
        _unsubscribeClosed = SshNetInternals.SubscribeClosed(_channel, () => { });

        SshNetInternals.SendShellRequests(_channel, terminalType, columns, rows, x11);
    }

    public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        SshNetInternals.SendChannelData(_channel, data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        SshNetInternals.SendWindowChangeRequest(_channel, (uint)cols, (uint)rows, 0u, 0u);
        return ValueTask.CompletedTask;
    }

    private static ReadOnlyMemory<byte> NormalizeBannerBytes(string banner)
    {
        var normalised = banner.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalised);
    }

    /// <summary>Closes the underlying channel. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unsubscribeData();
        _unsubscribeClosed();
        try { SshNetInternals.CloseChannel(_channel); }
        catch { /* already closed/disconnected */ }
    }
}
