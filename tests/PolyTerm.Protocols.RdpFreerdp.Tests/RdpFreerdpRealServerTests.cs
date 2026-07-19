using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.RdpFreerdp.Tests;

/// <summary>
/// Real, end-to-end coverage of the FreeRDP-backed RDP module (issue #67) against a real Windows RDP
/// host — connect, receive real framebuffer data, send a benign input event, disconnect cleanly.
/// Needs a real, reachable RDP-enabled Windows host, not a resource CI or every dev machine has, so
/// this skips gracefully (not a failure) when the host/username/password environment variables below
/// aren't set, matching this repo's existing precedent for other real-infrastructure-dependent tests
/// (e.g. <c>SshAutoReconnectTests</c>/<c>SshX11InboundBridgeTests</c> in the SSH/FlaUI suites). None of
/// this test's real server details are committed to the repo - they're only ever set in this
/// developer's own local environment.
/// </summary>
public sealed class RdpFreerdpRealServerTests
{
    private static readonly string? TestHost = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_HOST");
    private static readonly string? TestUsername = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_USERNAME");
    private static readonly string? TestPassword = Environment.GetEnvironmentVariable("POLYTERM_TEST_RDP_PASSWORD");

    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    [Fact]
    public async Task ConnectAsync_AgainstARealWindowsHost_ReceivesFramebufferDataAndAcceptsInput()
    {
        if (string.IsNullOrEmpty(TestHost) || string.IsNullOrEmpty(TestUsername) || string.IsNullOrEmpty(TestPassword))
        {
            Console.WriteLine("SKIP: POLYTERM_TEST_RDP_HOST/POLYTERM_TEST_RDP_USERNAME/POLYTERM_TEST_RDP_PASSWORD not set - this test needs a real RDP-enabled Windows host. See this class's own doc comment.");
            return;
        }

        var descriptor = new RdpFreerdpProtocolDescriptor(NullLoggerFactory.Instance);
        descriptor.IsAvailable(out var reason).Should().BeTrue(reason ?? "native shim should load in this test environment");

        var profile = new ConnectionProfile
        {
            Name = "rdp-freerdp-real-server-test",
            ProtocolId = "rdp",
            Host = TestHost,
        };
        using var credential = new CredentialMaterial
        {
            Username = TestUsername,
            Secret = Encoding.UTF8.GetBytes(TestPassword),
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(),
            Profile = profile,
            Credential = credential,
            HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        };

        await using var session = descriptor.CreateSession(ctx);

        var regionUpdated = new TaskCompletionSource<FramebufferRegionUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRegionUpdated(object? sender, FramebufferRegionUpdatedEventArgs e) => regionUpdated.TrySetResult(e);

        try
        {
            await session.ConnectAsync();
            session.State.Should().Be(SessionState.Connected);

            var channel = session.Framebuffer;
            channel.Should().NotBeNull("RDP is a Framebuffer-surface protocol");
            channel!.RegionUpdated += OnRegionUpdated;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (timeoutCts.Token.Register(() => regionUpdated.TrySetCanceled()))
            {
                // The very first RegionUpdated is just gdi_init's blank initial canvas (all-zero) -
                // fired before the server's first real bitmap update arrives. Assert structure on it,
                // but only assert real pixel content after giving the server a few seconds to actually
                // paint the desktop (confirmed against the real test host: a 350ms-total test run that
                // asserted on this first event alone saw nothing but zeros).
                var firstRegion = await regionUpdated.Task;
                firstRegion.Width.Should().BeGreaterThan(0);
                firstRegion.Height.Should().BeGreaterThan(0);
                firstRegion.Data.Length.Should().Be(firstRegion.Width * firstRegion.Height * 4);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));

            var snapshot = channel.CaptureSnapshot();
            snapshot.Should().NotBeNull();
            snapshot!.Should().Contain(b => b != 0, "a real remote desktop frame should not be entirely black/transparent after several seconds connected");

            // Benign - just proves input injection round-trips through the shim without erroring.
            await channel.SendPointerAsync(new PointerEvent { Kind = PointerEventKind.Move, Button = PointerButton.None, X = 10, Y = 10 });
        }
        finally
        {
            if (session.Framebuffer is { } ch) ch.RegionUpdated -= OnRegionUpdated;
            await session.DisconnectAsync();
        }

        session.State.Should().Be(SessionState.Closed);
    }
}
