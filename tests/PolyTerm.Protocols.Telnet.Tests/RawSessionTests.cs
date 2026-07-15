using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Telnet.Tests;

/// <summary>
/// Coverage for <see cref="RawSession"/> — the simplest member of the Telnet family, deliberately no
/// protocol framing at all. No fake protocol server needed here; a plain loopback echo listener is
/// enough to prove bytes pass straight through in both directions unmodified.
/// </summary>
public sealed class RawSessionTests
{
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Raw TCP sessions never evaluate a host key policy.");
    }

    private static SessionContext MakeContext(string? host, int? port) => new()
    {
        SessionId = Guid.NewGuid(),
        Profile = new ConnectionProfile { Name = "raw-test", ProtocolId = "raw", Host = host, Port = port },
        HostKeyPolicy = new UnusedHostKeyPolicy(),
    };

    [Fact]
    public async Task ConnectAsync_ThrowsWhenNoPortConfigured()
    {
        await using var session = new RawSession(MakeContext("127.0.0.1", null), NullLogger<RawSession>.Instance);

        var act = () => session.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No port configured.", "unlike Telnet/rlogin, raw TCP has no sensible default port to fall back to");
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenNoHostConfigured()
    {
        await using var session = new RawSession(MakeContext(null, 12345), NullLogger<RawSession>.Instance);

        var act = () => session.ConnectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No host configured.");
    }

    [Fact]
    public async Task ConnectAsync_EstablishesPassthroughConnection_BytesRoundTripUnmodifiedBothWays()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // A trivial echo server: whatever the client sends, it gets straight back.
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverCts.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var buf = new byte[4096];
            var read = await stream.ReadAsync(buf, serverCts.Token).ConfigureAwait(false);
            await stream.WriteAsync(buf.AsMemory(0, read), serverCts.Token).ConfigureAwait(false);
            await Task.Delay(300, serverCts.Token).ConfigureAwait(false);
        }, serverCts.Token);

        try
        {
            await using var session = new RawSession(MakeContext("127.0.0.1", port), NullLogger<RawSession>.Instance);
            await session.ConnectAsync(serverCts.Token);

            session.State.Should().Be(SessionState.Connected);
            session.Terminal.Should().NotBeNull();
            session.Terminal!.TerminalType.Should().Be("dumb");

            var received = new List<byte>();
            var gotEcho = new TaskCompletionSource();
            session.Terminal.OutputReceived += (_, chunk) =>
            {
                received.AddRange(chunk.ToArray());
                gotEcho.TrySetResult();
            };

            var payload = "raw-passthrough-test"u8.ToArray();
            await session.Terminal.SendInputAsync(payload, serverCts.Token);

            var completed = await Task.WhenAny(gotEcho.Task, Task.Delay(TimeSpan.FromSeconds(5), serverCts.Token));
            completed.Should().Be(gotEcho.Task, "the echo server should reply with exactly what was sent");
            received.Should().Equal(payload, "raw TCP has no protocol framing — bytes must round-trip completely unmodified");
        }
        finally
        {
            listener.Stop();
            try { await serverTask; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }
}
