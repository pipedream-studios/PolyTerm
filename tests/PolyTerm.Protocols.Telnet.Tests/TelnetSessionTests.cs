using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Telnet.Tests;

/// <summary>
/// End-to-end coverage of <see cref="TelnetSession"/>/<see cref="TelnetTerminalChannel"/> against a
/// real (fake) Telnet server over a real loopback socket — first automated coverage of any kind for
/// this protocol module (issue #65). Covers the IAC option-negotiation stripping/refusal logic that's
/// the one genuinely non-trivial piece of this module; connect/disconnect lifecycle and plain
/// pass-through are the same shape already proven for raw TCP.
/// </summary>
public sealed class TelnetSessionTests
{
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Telnet sessions never evaluate a host key policy.");
    }

    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251;

    private static SessionContext MakeContext(int port) => new()
    {
        SessionId = Guid.NewGuid(),
        Profile = new ConnectionProfile { Name = "telnet-test", ProtocolId = "telnet", Host = "127.0.0.1", Port = port },
        HostKeyPolicy = new UnusedHostKeyPolicy(),
    };

    /// <summary>Accumulates every chunk delivered via <see cref="ITerminalChannel.OutputReceived"/>, in order.</summary>
    private static (List<byte> Received, Func<int, TimeSpan, CancellationToken, Task> WaitForLength) SubscribeAndAccumulate(ITerminalChannel terminal)
    {
        var received = new List<byte>();
        var gotData = new TaskCompletionSource();
        var lockObj = new object();
        terminal.OutputReceived += (_, chunk) =>
        {
            lock (lockObj) received.AddRange(chunk.ToArray());
            gotData.TrySetResult();
        };

        async Task WaitForLength(int minLength, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (lockObj)
                    if (received.Count >= minLength) return;
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }

        return (received, WaitForLength);
    }

    [Fact]
    public async Task ConnectAsync_ConnectsSuccessfully_WithXtermTerminalType()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        session.State.Should().Be(SessionState.Connected);
        session.Terminal.Should().NotBeNull();
        session.Terminal!.TerminalType.Should().Be("xterm-256color");
    }

    [Fact]
    public async Task PlainData_WithNoNegotiation_RoundTripsUnmodified()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var (received, waitForLength) = SubscribeAndAccumulate(session.Terminal!);

        var payload = "login: "u8.ToArray();
        await server.SendAsync(payload, cts.Token);
        await waitForLength(payload.Length, TimeSpan.FromSeconds(5), cts.Token);

        received.Should().Equal(payload, "plain bytes with no IAC sequences must pass through byte-for-byte");
    }

    [Fact]
    public async Task IacWillOffer_IsStrippedFromOutput_AndClientRepliesDont()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var (received, waitForLength) = SubscribeAndAccumulate(session.Terminal!);

        const byte echoOption = 1; // ECHO
        byte[] message = ["a"u8[0], IAC, WILL, echoOption, "b"u8[0]];
        await server.SendAsync(message, cts.Token);
        await waitForLength(2, TimeSpan.FromSeconds(5), cts.Token);

        received.Should().Equal(["a"u8[0], "b"u8[0]], "the IAC WILL sequence must be stripped, leaving only the surrounding plain bytes");

        var reply = await server.WaitForReceivedBytesAsync(3, TimeSpan.FromSeconds(5), cts.Token);
        reply.Should().Equal([IAC, DONT, echoOption], "RFC 854: a minimal client that refuses everything replies DONT to a WILL offer");
    }

    [Fact]
    public async Task IacDoOffer_ClientRepliesWont()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var (_, _) = SubscribeAndAccumulate(session.Terminal!); // starts the read loop

        const byte suppressGoAhead = 3;
        byte[] message = [IAC, DO, suppressGoAhead];
        await server.SendAsync(message, cts.Token);

        var reply = await server.WaitForReceivedBytesAsync(3, TimeSpan.FromSeconds(5), cts.Token);
        reply.Should().Equal([IAC, WONT, suppressGoAhead], "RFC 854: a minimal client that refuses everything replies WONT to a DO offer");
    }

    [Fact]
    public async Task IacWontAndDont_GetNoReply()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var (received, waitForLength) = SubscribeAndAccumulate(session.Terminal!);

        const byte someOption = 24; // TERMINAL-TYPE
        byte[] message = [IAC, WONT, someOption, IAC, DONT, someOption, "x"u8[0]];
        await server.SendAsync(message, cts.Token);
        await waitForLength(1, TimeSpan.FromSeconds(5), cts.Token);

        received.Should().Equal(["x"u8[0]], "both negotiation sequences must still be stripped from the data stream even though neither gets a reply");

        // No reply should ever arrive for WONT/DONT — assert nothing shows up within a bounded window
        // rather than asserting forever (a false negative here would just mean the wait was too short).
        var reply = await server.WaitForReceivedBytesAsync(1, TimeSpan.FromMilliseconds(500), cts.Token);
        reply.Should().BeEmpty("RFC 854 requires no reply to WONT/DONT, to avoid infinite negotiation loops");
    }

    [Fact]
    public async Task EscapedIacIac_ProducesALiteral0xFFByteInTheData()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var (received, waitForLength) = SubscribeAndAccumulate(session.Terminal!);

        byte[] message = ["x"u8[0], IAC, IAC, "y"u8[0]]; // escaped IAC IAC = one literal 0xFF byte
        await server.SendAsync(message, cts.Token);
        await waitForLength(3, TimeSpan.FromSeconds(5), cts.Token);

        received.Should().Equal(["x"u8[0], IAC, "y"u8[0]], "IAC IAC is the RFC 854 escape for a literal 0xFF data byte, not a command");
    }

    [Fact]
    public async Task ResizeAsync_SendsTheNawsSubnegotiationSequence()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        await session.Terminal!.ResizeAsync(80, 24, cts.Token);

        const byte SB = 250, SE = 240, NAWS = 31;
        var reply = await server.WaitForReceivedBytesAsync(9, TimeSpan.FromSeconds(5), cts.Token);
        reply.Should().Equal([IAC, SB, NAWS, 0, 80, 0, 24, IAC, SE], "IAC SB NAWS <cols hi/lo> <rows hi/lo> IAC SE per the standard NAWS wire format");
    }

    [Fact]
    public async Task SendInputAsync_WritesRawBytesToTheServer_Unmodified()
    {
        await using var server = new FakeTelnetServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new TelnetSession(MakeContext(server.Port), NullLogger<TelnetSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var typed = "whoami\r\n"u8.ToArray();
        await session.Terminal!.SendInputAsync(typed, cts.Token);

        var reply = await server.WaitForReceivedBytesAsync(typed.Length, TimeSpan.FromSeconds(5), cts.Token);
        reply.Should().Equal(typed, "outbound bytes are never IAC-escaped by this minimal client — SendInputAsync is plain pass-through");
    }
}
