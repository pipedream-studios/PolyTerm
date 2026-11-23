using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// End-to-end against a real, ad-hoc Windows OpenSSH server (<see cref="FakeSshServer"/>) — not
/// mocks, not a hand-rolled protocol implementation. Requires the Windows "OpenSSH Server" optional
/// feature to be installed; see docs/testing.md's SSH section for why this approach was chosen over
/// hand-rolling SSH-2 (unlike RFB, real cryptographic key exchange makes that impractical) and over
/// Docker (none available in this environment).
/// </summary>
public sealed class SshSessionTests
{
    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    /// <summary>Records every log entry's level and message, so tests can check what was (not) logged at Error.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static Task<SshSession> ConnectAsync(FakeSshServer server, CancellationToken ct) =>
        ConnectAsync(server, NullLogger<SshSession>.Instance, ct);

    private static async Task<SshSession> ConnectAsync(FakeSshServer server, ILogger<SshSession> logger, CancellationToken ct)
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["privateKeyPath"] = JsonSerializer.SerializeToElement(server.ClientPrivateKeyPath),
        };
        var profile = new ConnectionProfile
        {
            Name = "fake-ssh", ProtocolId = "ssh", Host = "127.0.0.1", Port = server.Port,
            Settings = settings,
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
            // Supplied so that, if a test's session wrongly enters Reconnecting, the reconnect can
            // actually succeed and show up as a state change rather than stalling unnoticed.
            RefreshCredentialsAsync = _ => Task.FromResult(new ReconnectCredentials()),
        };

        var session = new SshSession(ctx, logger, NullBundledX11ServerProvider.Instance);
        await session.ConnectAsync(ct).ConfigureAwait(false);
        return session;
    }

    [Fact]
    public async Task Connect_ToAHostWithSftp_ExposesFileTransferAndNoUnavailableReason()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var session = await ConnectAsync(server, cts.Token);

        session.FileTransfer.Should().NotBeNull();
        session.FileTransferUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task Connect_ToAHostWithoutSftp_StillConnects_AndSaysWhyFileTransferIsUnavailable()
    {
        await using var server = await FakeSshServer.StartAsync(withSftp: false, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var session = await ConnectAsync(server, cts.Token);

        session.State.Should().Be(SessionState.Connected, "SFTP is best-effort and must not abort the session");
        session.Terminal.Should().NotBeNull();
        session.FileTransfer.Should().BeNull();
        session.FileTransferUnavailableReason.Should().Be(SshSession.SftpNotOfferedReason,
            "a server without an sftp subsystem is a normal configuration, not a failure");
    }

    [Fact]
    public async Task ConnectAndExecuteCommand_ReceivesRealOutput()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var session = await ConnectAsync(server, cts.Token);
        session.Terminal.Should().NotBeNull();

        var outputReceived = new TaskCompletionSource<string>();
        var buffer = new StringBuilder();
        session.Terminal!.OutputReceived += (_, data) =>
        {
            buffer.Append(Encoding.UTF8.GetString(data.Span));
            if (buffer.ToString().Contains("polyterm-ssh-test-marker"))
                outputReceived.TrySetResult(buffer.ToString());
        };

        await session.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("echo polyterm-ssh-test-marker\r\n"), cts.Token);

        var completed = await Task.WhenAny(outputReceived.Task, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        completed.Should().Be(outputReceived.Task, "the real sshd should have echoed the command's output by now");

        (await outputReceived.Task).Should().Contain("polyterm-ssh-test-marker");
    }

    [Fact]
    public async Task LocalTunnel_ActuallyForwardsTraffic()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // A plain TCP echo listener standing in for "the remote destination" — since everything here
        // runs on loopback through the real sshd, this proves the forward genuinely round-trips
        // through the SSH server rather than just directly connecting (which would happen if
        // OpenLocalAsync were silently a no-op).
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        var echoPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;
        var echoTask = AcceptAndEchoOnceAsync(echoListener, cts.Token);

        var session = await ConnectAsync(server, cts.Token);
        session.Tunnels.Should().NotBeNull();

        using var forwardListener = new TcpListener(IPAddress.Loopback, 0);
        forwardListener.Start();
        var localPort = ((IPEndPoint)forwardListener.LocalEndpoint).Port;
        forwardListener.Stop(); // release it so OpenLocalAsync can bind the same port

        await session.Tunnels!.OpenLocalAsync(localPort, "127.0.0.1", echoPort, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort, cts.Token);
        var payload = Encoding.UTF8.GetBytes("hello-through-the-tunnel");
        await client.GetStream().WriteAsync(payload, cts.Token);

        var responseBuffer = new byte[payload.Length];
        var read = 0;
        while (read < responseBuffer.Length)
        {
            var n = await client.GetStream().ReadAsync(responseBuffer.AsMemory(read), cts.Token);
            if (n == 0) break;
            read += n;
        }

        Encoding.UTF8.GetString(responseBuffer, 0, read).Should().Be("hello-through-the-tunnel");
        await echoTask;
    }

    /// <summary>
    /// Two real sshd instances — a "bastion" and a "target" — connected through PolyTerm's own
    /// jump-host chaining logic (<c>SshSession.ConnectCoreAsync</c>'s manual per-hop
    /// <c>ForwardedPortLocal</c> chain — SSH.NET has no native ProxyJump support). If the chain logic
    /// were broken (e.g. connecting straight to the target and ignoring the bastion), this would
    /// simply fail to authenticate/connect, since the target's host key and port are only reachable
    /// in this test through the bastion's own forwarded port.
    /// </summary>
    [Fact]
    public async Task JumpHostChain_ConnectsThroughBastionToTarget()
    {
        await using var bastion = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        await using var target = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var bastionProfile = new ConnectionProfile
        {
            Name = "bastion", ProtocolId = "ssh", Host = "127.0.0.1", Port = bastion.Port,
            Settings = new Dictionary<string, JsonElement>
            {
                ["privateKeyPath"] = JsonSerializer.SerializeToElement(bastion.ClientPrivateKeyPath),
            },
        };
        var targetProfile = new ConnectionProfile
        {
            Name = "target", ProtocolId = "ssh", Host = "127.0.0.1", Port = target.Port,
            Settings = new Dictionary<string, JsonElement>
            {
                ["privateKeyPath"] = JsonSerializer.SerializeToElement(target.ClientPrivateKeyPath),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = targetProfile,
            HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
            JumpChain = [new JumpHopContext { Profile = bastionProfile, Credential = null }],
        };

        var session = new SshSession(ctx, NullLogger<SshSession>.Instance, NullBundledX11ServerProvider.Instance);
        await session.ConnectAsync(cts.Token);

        session.Terminal.Should().NotBeNull();
        var outputReceived = new TaskCompletionSource<string>();
        var buffer = new StringBuilder();
        session.Terminal!.OutputReceived += (_, data) =>
        {
            buffer.Append(Encoding.UTF8.GetString(data.Span));
            if (buffer.ToString().Contains("polyterm-jump-host-marker"))
                outputReceived.TrySetResult(buffer.ToString());
        };

        await session.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("echo polyterm-jump-host-marker\r\n"), cts.Token);

        var completed = await Task.WhenAny(outputReceived.Task, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        completed.Should().Be(outputReceived.Task, "the target sshd, reached via the bastion, should have echoed the command's output by now");
    }

    private static async Task AcceptAndEchoOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        using var stream = client.GetStream();
        var buffer = new byte[256];
        var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        await stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
    }

    /// <summary>Sends <c>echo &lt;marker&gt;</c> through <paramref name="session"/>'s terminal and waits for the real shell to echo it back — the same liveness check used before and after the reconnect in <see cref="AutoReconnect_RecoversAfterServerRestart_AndTheSessionWorksAgainAfterwards"/>.</summary>
    private static async Task AssertRealCommandRoundTripsAsync(SshSession session, string marker, CancellationToken ct)
    {
        var outputReceived = new TaskCompletionSource<string>();
        var buffer = new StringBuilder();
        void OnOutput(object? sender, ReadOnlyMemory<byte> data)
        {
            buffer.Append(Encoding.UTF8.GetString(data.Span));
            if (buffer.ToString().Contains(marker))
                outputReceived.TrySetResult(buffer.ToString());
        }

        session.Terminal.Should().NotBeNull($"a terminal channel must exist to send '{marker}'");
        session.Terminal!.OutputReceived += OnOutput;
        try
        {
            await session.Terminal.SendInputAsync(Encoding.UTF8.GetBytes($"echo {marker}\r\n"), ct);
            var completed = await Task.WhenAny(outputReceived.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            completed.Should().Be(outputReceived.Task, $"the real sshd should have echoed '{marker}' by now");
            (await outputReceived.Task).Should().Contain(marker);
        }
        finally
        {
            session.Terminal.OutputReceived -= OnOutput;
        }
    }

    /// <summary>
    /// Real auto-reconnect, end-to-end: kills the real <c>sshd.exe</c> mid-session (simulating a
    /// dropped connection), restarts it on the exact same port, and confirms <see cref="SshSession"/>'s
    /// own backoff loop (<c>ReconnectLoopAsync</c>) notices the drop, retries, and recovers on its
    /// own — with a real, freshly-wired, working terminal channel afterward, not just
    /// <see cref="SessionState"/> flipping back to <c>Connected</c>. `RefreshCredentialsAsync` is
    /// supplied manually here (trivial — key-file auth reads straight from
    /// <c>ConnectionProfile.Settings</c>, not from <c>CredentialMaterial</c>) since this test builds
    /// <see cref="SessionContext"/> directly rather than going through <c>SessionManager</c>, which
    /// normally provides it for real profiles.
    /// </summary>
    [Fact]
    public async Task AutoReconnect_RecoversAfterServerRestart_AndTheSessionWorksAgainAfterwards()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var settings = new Dictionary<string, JsonElement>
        {
            ["privateKeyPath"] = JsonSerializer.SerializeToElement(server.ClientPrivateKeyPath),
        };
        var profile = new ConnectionProfile
        {
            Name = "fake-ssh-reconnect", ProtocolId = "ssh", Host = "127.0.0.1", Port = server.Port,
            Settings = settings,
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = profile,
            HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
            RefreshCredentialsAsync = _ => Task.FromResult(new ReconnectCredentials()),
        };

        var session = new SshSession(ctx, NullLogger<SshSession>.Instance, NullBundledX11ServerProvider.Instance);
        await session.ConnectAsync(cts.Token);

        await AssertRealCommandRoundTripsAsync(session, "polyterm-before-reconnect-marker", cts.Token);

        var sawReconnecting = false;
        var reconnectedTcs = new TaskCompletionSource();
        session.StateChanged += (_, e) =>
        {
            if (e.Current == SessionState.Reconnecting) sawReconnecting = true;
            if (sawReconnecting && e.Current == SessionState.Connected) reconnectedTcs.TrySetResult();
        };

        await server.StopAsync();
        // Give SSH.NET a moment to notice the dropped connection (its shell-stream read loop should
        // see the reset promptly on loopback) before the server comes back, so this genuinely
        // exercises "was disconnected, then recovered" rather than racing a same-instant restart.
        await Task.Delay(500, cts.Token);
        await server.RestartOnSamePortAsync(cts.Token);

        var completed = await Task.WhenAny(reconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(45), cts.Token));
        completed.Should().Be(reconnectedTcs.Task, "SshSession's own backoff loop should reconnect once the server is back, without any external help");
        sawReconnecting.Should().BeTrue("the session should have visibly entered Reconnecting at some point");
        session.State.Should().Be(SessionState.Connected);

        await AssertRealCommandRoundTripsAsync(session, "polyterm-after-reconnect-marker", cts.Token);
    }
    /// <summary>
    /// Issue #264: a remote <c>exit</c> ends the shell, and the server then closes the connection.
    /// That is a normal end of session, so the session must reach <see cref="SessionState.Closed"/>
    /// and never pass through <see cref="SessionState.Reconnecting"/>, and nothing is logged at Error.
    /// </summary>
    [Fact]
    public async Task RemoteExit_ClosesTheSession_WithoutReconnecting()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var logger = new RecordingLogger<SshSession>();

        await using var session = await ConnectAsync(server, logger, cts.Token);
        await AssertRealCommandRoundTripsAsync(session, "polyterm-before-exit-marker", cts.Token);

        var states = new System.Collections.Concurrent.ConcurrentQueue<SessionState>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, e) =>
        {
            states.Enqueue(e.Current);
            if (e.Current == SessionState.Closed) closed.TrySetResult();
        };

        await session.Terminal!.SendInputAsync(Encoding.UTF8.GetBytes("exit\r\n"), cts.Token);

        var completed = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
        completed.Should().Be(closed.Task, "the session should close once the remote shell exits");

        // The first reconnect attempt would start 2s after a wrongly detected drop; wait past that.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        states.Should().NotContain(SessionState.Reconnecting, "a remote exit is not a dropped connection");
        session.State.Should().Be(SessionState.Closed);
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty();
    }

    /// <summary>
    /// Issue #264: closing the session ourselves is a normal teardown, so whatever connection error
    /// SSH.NET reports along the way must not be logged at Error, and the session ends Closed.
    /// </summary>
    [Fact]
    public async Task UserDisconnect_DoesNotLogAtError()
    {
        await using var server = await FakeSshServer.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var logger = new RecordingLogger<SshSession>();

        var session = await ConnectAsync(server, logger, cts.Token);
        await AssertRealCommandRoundTripsAsync(session, "polyterm-before-disconnect-marker", cts.Token);

        await session.DisconnectAsync(cts.Token);
        await session.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token); // let any late SSH.NET error arrive

        session.State.Should().Be(SessionState.Closed);
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty();
    }
}
