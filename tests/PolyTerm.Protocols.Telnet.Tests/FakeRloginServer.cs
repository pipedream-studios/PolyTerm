using System.Net;
using System.Net.Sockets;

namespace PolyTerm.Protocols.Telnet.Tests;

/// <summary>
/// A minimal, hand-rolled rlogin (RFC 1282) "server" — accepts one connection, reads the client's
/// one-shot <c>\0localUser\0remoteUser\0terminalType\0</c> handshake, and replies with a single
/// configurable ack byte (0 = accept, non-zero = reject), matching exactly what
/// <see cref="RloginSession.ConnectChannelAsync"/> sends/expects.
/// </summary>
internal sealed class FakeRloginServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte _ackByte;
    private Task? _acceptTask;
    private byte[] _receivedHandshake = [];

    public int Port { get; }

    public FakeRloginServer(byte ackByte = 0)
    {
        _ackByte = ackByte;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start(CancellationToken ct) => _acceptTask = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        using var stream = client.GetStream();

        // RloginSession sends the whole handshake in one SendAsync call, so a single read is enough
        // to capture it over loopback — same simplifying assumption FakeRfbServer makes for RFB's own
        // small fixed-size handshake messages.
        var buf = new byte[512];
        var read = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
        _receivedHandshake = buf[..read];

        await stream.WriteAsync(new[] { _ackByte }, ct).ConfigureAwait(false);

        // Keep the connection open briefly so the client's ConnectChannelAsync fully observes the ack
        // before the server tears down — mirrors FakeRfbServer's own post-response settle delay.
        await Task.Delay(300, ct).ConfigureAwait(false);
    }

    /// <summary>The raw handshake bytes the client sent, once available. Empty until a client has connected and sent them.</summary>
    public byte[] ReceivedHandshake => _receivedHandshake;

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }
}
