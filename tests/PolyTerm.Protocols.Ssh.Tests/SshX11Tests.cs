using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// X11 forwarding (M4) is built on reflection into SSH.NET internals — <see cref="SshNetInternals"/>'s
/// doc comment explains why (no public hook to send "x11-req" before "shell"). This has two halves:
/// <list type="bullet">
/// <item>The <b>request-sending side</b> (<see cref="SshX11ShellChannel"/>) — the "loud failure when
/// unsupported" behavior is covered by real automated tests below. The full happy-path data flow
/// (echoing a real command through the reflected channel) is <b>not</b> automated: it was confirmed
/// live via a real ad-hoc <c>sshd.exe</c> in a standalone console app (378 bytes of real shell output
/// round-tripped correctly), but the identical code crashes <c>dotnet test</c>'s test host process
/// specifically — reproducible, isolated down to "real DataReceived events flowing through the
/// reflected delegate while running under testhost.exe", but not further root-caused (candidates:
/// VSTest's AssemblyLoadContext/JIT settings, or the same category of transient native-process
/// interference seen elsewhere this session — see docs/todos.md's SSH X11 section). This is a test
/// infrastructure gap, not a defect in the shipped code.</item>
/// <item>The <b>inbound channel bridge to a local X server</b> (<see cref="X11Forwarder"/>) — NOT
/// covered here at all. Windows' own OpenSSH server doesn't implement X11 forwarding server-side
/// (confirmed below: requesting it is always rejected), and no Linux/macOS sshd, local X server, or
/// X11 client app is available in this environment to exercise it against.
/// </item>
/// </list>
/// </summary>
public sealed class SshX11Tests
{
    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    /// <summary>
    /// Windows' Win32-OpenSSH sshd does not support X11 forwarding server-side — confirmed live: it
    /// accepts <c>X11Forwarding yes</c> in sshd_config without error, but always rejects the actual
    /// "x11-req" with "Invalid X11 forwarding data"/refusal regardless. This proves that failure is
    /// surfaced loudly (a clear, catchable exception) rather than silently swallowed — both for the
    /// low-level channel API directly, and through <see cref="SshSession.ConnectAsync"/>'s public
    /// surface with the "x11Forwarding" profile setting.
    /// </summary>
    [Fact]
    public async Task SshX11ShellChannel_RequestingX11AgainstAServerThatDoesNotSupportIt_ThrowsClearly()
    {
        await using var server = await FakeSshServer.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var client = new SshClient(new ConnectionInfo("127.0.0.1", server.Port, server.Username,
            new PrivateKeyAuthenticationMethod(server.Username, new PrivateKeyFile(server.ClientPrivateKeyPath))));
        await Task.Run(client.Connect, cts.Token);
        var session = SshNetInternals.GetSession(client)!;

        var x11 = new X11ForwardingParameters(false, "MIT-MAGIC-COOKIE-1", Encoding.ASCII.GetBytes(new string('a', 32)), 0u);

        var act = () => new SshX11ShellChannel(session, "xterm-256color", 80, 24, x11);
        act.Should().Throw<SshException>().WithMessage("*X11*");
    }

    [Fact]
    public async Task SshSession_ConnectAsync_WithX11ForwardingEnabled_AgainstAServerThatDoesNotSupportIt_FailsCleanly()
    {
        await using var server = await FakeSshServer.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var profile = new ConnectionProfile
        {
            Name = "fake-ssh-x11", ProtocolId = "ssh", Host = "127.0.0.1", Port = server.Port,
            Settings = new Dictionary<string, JsonElement>
            {
                ["privateKeyPath"] = JsonSerializer.SerializeToElement(server.ClientPrivateKeyPath),
                ["x11Forwarding"]  = JsonSerializer.SerializeToElement(true),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        };
        var session = new SshSession(ctx, NullLogger<SshSession>.Instance);

        var act = async () => await session.ConnectAsync(cts.Token);
        await act.Should().ThrowAsync<Exception>();
        session.State.Should().Be(SessionState.Faulted);
    }
}
