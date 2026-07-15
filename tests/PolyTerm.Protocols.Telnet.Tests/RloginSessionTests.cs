using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Telnet.Tests;

/// <summary>
/// End-to-end coverage of <see cref="RloginSession"/>'s RFC 1282 handshake (send
/// <c>\0localUser\0remoteUser\0terminalType\0</c>, wait for a single-byte ack) against a real fake
/// server over a real loopback socket.
/// </summary>
public sealed class RloginSessionTests
{
    private sealed class UnusedHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            throw new InvalidOperationException("Rlogin sessions never evaluate a host key policy.");
    }

    private static SessionContext MakeContext(int port, string? remoteUsername = null, string? localUsername = null)
    {
        var settings = new Dictionary<string, JsonElement>();
        if (remoteUsername is not null) settings["username"] = JsonSerializer.SerializeToElement(remoteUsername);
        if (localUsername is not null) settings["localUsername"] = JsonSerializer.SerializeToElement(localUsername);

        return new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = new ConnectionProfile { Name = "rlogin-test", ProtocolId = "rlogin", Host = "127.0.0.1", Port = port, Settings = settings },
            HostKeyPolicy = new UnusedHostKeyPolicy(),
        };
    }

    [Fact]
    public async Task ConnectAsync_SendsTheExactRfc1282Handshake_UsingConfiguredUsernames()
    {
        await using var server = new FakeRloginServer(ackByte: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new RloginSession(
            MakeContext(server.Port, remoteUsername: "remoteuser", localUsername: "localuser"),
            NullLogger<RloginSession>.Instance);
        await session.ConnectAsync(cts.Token);

        session.State.Should().Be(SessionState.Connected);

        // Poll rather than a fixed delay — the server has already ack'd by the time ConnectAsync
        // returns, but the handshake bytes were written to _receivedHandshake on the server's own
        // background accept task, which may not have observed by this exact instant.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (server.ReceivedHandshake.Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, cts.Token);

        var expected = Encoding.ASCII.GetBytes("\0localuser\0remoteuser\0xterm-256color/38400\0");
        server.ReceivedHandshake.Should().Equal(expected, "RFC 1282: \\0localUser\\0remoteUser\\0terminalType/speed\\0");
    }

    [Fact]
    public async Task ConnectAsync_SucceedsAndBecomesPassthrough_WhenServerAcksWithZero()
    {
        await using var server = new FakeRloginServer(ackByte: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new RloginSession(MakeContext(server.Port, "u", "u"), NullLogger<RloginSession>.Instance);
        await session.ConnectAsync(cts.Token);

        session.State.Should().Be(SessionState.Connected);
        session.Terminal.Should().NotBeNull();
        session.Terminal!.TerminalType.Should().Be("xterm-256color", "post-handshake the channel becomes plain pass-through with a real terminal type");
    }

    [Fact]
    public async Task ConnectAsync_ThrowsAndFaults_WhenServerAcksWithNonZero()
    {
        await using var server = new FakeRloginServer(ackByte: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new RloginSession(MakeContext(server.Port, "u", "u"), NullLogger<RloginSession>.Instance);

        var act = () => session.ConnectAsync(cts.Token);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unexpected rlogin handshake response.");
        session.State.Should().Be(SessionState.Faulted);
    }

    [Fact]
    public async Task ConnectAsync_FallsBackToEnvironmentUserName_WhenNoUsernameSettingsProvided()
    {
        await using var server = new FakeRloginServer(ackByte: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        await using var session = new RloginSession(MakeContext(server.Port), NullLogger<RloginSession>.Instance);
        await session.ConnectAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (server.ReceivedHandshake.Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, cts.Token);

        var expected = Encoding.ASCII.GetBytes($"\0{Environment.UserName}\0{Environment.UserName}\0xterm-256color/38400\0");
        server.ReceivedHandshake.Should().Equal(expected, "with no username/localUsername settings and no credential, both fall back to the current OS user");
    }
}
