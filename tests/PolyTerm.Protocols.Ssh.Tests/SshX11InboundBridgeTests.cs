using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Protocols.Ssh.X11;
using Renci.SshNet.Common;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// Closes out the one thing <see cref="SshX11Tests"/> explicitly couldn't cover: the inbound
/// channel-to-local-X-server bridge (<see cref="X11.X11Forwarder"/>), against a real Linux sshd with
/// real X11 forwarding enabled (Ubuntu 24.04, <c>X11Forwarding yes</c> in
/// <c>/etc/ssh/sshd_config.d/polyterm-testing.conf</c>) and a real local X server.
/// </summary>
/// <remarks>
/// The local X server is external, manually-provisioned infrastructure, same convention as the SSH test
/// key (see <see cref="SshAutoReconnectTests"/>'s sibling doc comment in the FlaUI test suite) - this
/// test skips gracefully if it isn't set up. What was actually used to verify this: MobaXterm's bundled
/// Cygwin/X server (<c>XWin_MobaX_1.20.4.exe</c>), started with a real generated xauth cookie
/// (<c>-auth &lt;DefaultXauthorityPath&gt; -listen tcp -multiwindow</c>) so this also exercises real
/// cookie substitution (<see cref="X11.XauthCookieStore"/>/<see cref="X11.X11ConnectionSetupRewriter"/>,
/// closed out separately in #12) rather than a disable-access-control setup that would hide a broken
/// substitution path. Verification itself uses <c>xlsclients</c> (also bundled with MobaXterm) rather
/// than looking for a native window, so it's authoritative regardless of window-manager specifics: a
/// real client actually completing the X11 connection handshake is what <c>xlsclients</c> reports, not
/// just a window appearing on screen.
/// </remarks>
public sealed class SshX11InboundBridgeTests
{
    private static readonly string? TestHost = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_HOST");
    private static readonly string? TestUsername = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_USERNAME");
    private const string TargetClientName = "xclock";

    private static readonly string DefaultKeyPath = Path.Combine(
        Path.GetTempPath(), "claude", "D--code-PolyTerm", "72096780-787f-4b99-be25-e4392a21d0d4",
        "scratchpad", "flaui-test-keys", "ssh_test_server_key");

    private static readonly string DefaultXauthorityPath = Path.Combine(Path.GetTempPath(), "x11test", "Xauthority");

    private static readonly string DefaultXlsclientsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MobaXterm", "slash", "bin", "xlsclients.exe");

    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    [Fact]
    public async Task X11Forwarder_BridgesARealInboundXclockConnectionToTheLocalXServer()
    {
        var keyPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY") ?? DefaultKeyPath;
        if (!File.Exists(keyPath) || string.IsNullOrEmpty(TestHost) || string.IsNullOrEmpty(TestUsername))
        {
            Console.WriteLine($"SKIP: test SSH key not found at '{keyPath}', or POLYTERM_TEST_SSH_HOST/POLYTERM_TEST_SSH_USERNAME not set.");
            return;
        }

        var xauthPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_XAUTHORITY") ?? DefaultXauthorityPath;
        var xlsclientsPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_XLSCLIENTS") ?? DefaultXlsclientsPath;
        if (!File.Exists(xauthPath) || !File.Exists(xlsclientsPath))
        {
            Console.WriteLine($"SKIP: no local X server set up (expected an Xauthority at '{xauthPath}' and xlsclients at '{xlsclientsPath}').");
            return;
        }

        // The Xauthority file surviving on disk doesn't mean the X server process is still running (it
        // was started separately, by hand, and is just as easily stopped again by hand) - check it's
        // actually reachable too, so this skips gracefully rather than failing when it isn't.
        if (!X11DisplayEndpoint.Resolve("127.0.0.1:0", isWindows: true).IsReachable(TimeSpan.FromMilliseconds(500)))
        {
            Console.WriteLine("SKIP: no local X server currently listening on 127.0.0.1:6000.");
            return;
        }

        // XauthCookieStore reads this directly (see its own doc comment) - the real cookie the local X
        // server was started with, so X11ConnectionSetupRewriter has something real to substitute the
        // fake wire cookie for.
        Environment.SetEnvironmentVariable("XAUTHORITY", xauthPath);

        var profile = new ConnectionProfile
        {
            Name = "x11-inbound-bridge-test", ProtocolId = "ssh", Host = TestHost!, Port = 22,
            Settings = new Dictionary<string, JsonElement>
            {
                ["username"]       = JsonSerializer.SerializeToElement(TestUsername!),
                ["privateKeyPath"] = JsonSerializer.SerializeToElement(keyPath),
                ["x11Forwarding"]  = JsonSerializer.SerializeToElement(true),
            },
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        };
        var session = new SshSession(ctx, NullLogger<SshSession>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(cts.Token);
        session.State.Should().Be(SessionState.Connected);
        session.Terminal.Should().NotBeNull("a connected SSH session with x11Forwarding enabled should still have a normal shell channel");

        // "echo DISPLAY=..." and "xauth list" are printed real remote-side evidence (not asserted on -
        // just visible in test output) that sshd actually allocated a forwarded display and its own
        // xauth entry for this session, before xclock tries to use it.
        var outputBuf = new StringBuilder();
        session.Terminal!.OutputReceived += (_, data) => outputBuf.Append(Encoding.UTF8.GetString(data.Span));
        await session.Terminal!.SendInputAsync(Encoding.UTF8.GetBytes("echo DISPLAY=$DISPLAY; xauth list; xclock &\n"), cts.Token);

        var found = false;
        try
        {
            found = await WaitForClientAsync(xlsclientsPath, TargetClientName, TimeSpan.FromSeconds(15));
        }
        finally
        {
            Console.WriteLine($"Remote terminal output:\n{outputBuf}");
            // Best-effort cleanup regardless of outcome - the remote xclock process would otherwise
            // outlive this test (it isn't tied to the shell's own lifetime once backgrounded).
            try { await session.Terminal!.SendInputAsync(Encoding.UTF8.GetBytes("pkill xclock\n"), cts.Token); }
            catch { /* best-effort */ }
            await session.DisconnectAsync();
            await session.DisposeAsync();
        }

        found.Should().BeTrue(
            $"a real '{TargetClientName}' process on the remote host, run through a real inbound X11 " +
            "channel via X11Forwarder, should complete a real X11 connection handshake with the local " +
            "X server - confirmed independently via xlsclients, not just inferred from the SSH side");
    }

    private static async Task<bool> WaitForClientAsync(string xlsclientsPath, string clientName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var output = await RunXlsclientsAsync(xlsclientsPath).ConfigureAwait(false);
            if (output.Contains(clientName, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(500).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<string> RunXlsclientsAsync(string xlsclientsPath)
    {
        var psi = new ProcessStartInfo(xlsclientsPath)
        {
            ArgumentList = { "-display", "127.0.0.1:0", "-l" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start xlsclients.");
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        proc.WaitForExit(5000);
        return stdout;
    }
}
