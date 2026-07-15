using System.Net;
using System.Net.Sockets;

namespace PolyTerm.Protocols.Telnet.Tests;

/// <summary>
/// A minimal, hand-rolled Telnet "server" — accepts one connection, lets a test push arbitrary bytes
/// (plain data interleaved with real IAC option-negotiation sequences) to the connected
/// <see cref="TelnetSession"/>, and records whatever bytes the client sends back (its negotiation
/// replies), so <see cref="TelnetTerminalChannel"/>'s actual IAC handling can be verified against a
/// real socket round-trip rather than assumed. Telnet's own handshake is plain length-prefix-free
/// bytes (no cryptography), so hand-rolling it is practical the same way <c>FakeRfbServer</c> hand-
/// rolls RFB for VNC.
/// </summary>
internal sealed class FakeTelnetServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource _clientConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<byte> _received = [];
    private readonly object _receivedLock = new();
    private NetworkStream? _stream;
    private Task? _acceptTask;

    public int Port { get; }

    public FakeTelnetServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start(CancellationToken ct) => _acceptTask = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        _stream = client.GetStream();
        _clientConnected.TrySetResult();

        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (read == 0) break; // client closed
                lock (_receivedLock) _received.AddRange(buf.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // socket closed by teardown — expected
    }

    /// <summary>Sends bytes to the connected client, waiting for the connection first if it hasn't landed yet.</summary>
    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _clientConnected.Task.WaitAsync(ct).ConfigureAwait(false);
        await _stream!.WriteAsync(data, ct).ConfigureAwait(false);
    }

    /// <summary>Polls (rather than a single fixed delay) until at least <paramref name="minLength"/> bytes have arrived from the client, or <paramref name="timeout"/> elapses — matches this repo's existing UI-automation test convention for the same reason: a bare delay leaves no margin on a slower/contended machine.</summary>
    public async Task<byte[]> WaitForReceivedBytesAsync(int minLength, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_receivedLock)
                if (_received.Count >= minLength) return [.. _received];
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        lock (_receivedLock) return [.. _received];
    }

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
