using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Web.Tests;

/// <summary>
/// Covers <see cref="WebSession"/>'s default <c>ConnectAsync</c> path: an embedded-WebView tab
/// (<see cref="IProtocolSession.Web"/> populated, matching <c>BuildUrl</c>'s existing scheme/port
/// logic). The "openExternally" opt-out isn't covered here — it calls <c>Process.Start</c> with
/// <c>UseShellExecute = true</c>, which genuinely launches the OS default browser; doing that from an
/// automated unit test run would pop a real browser window on every build (and behave unpredictably
/// on CI runners with no configured default browser). That path is exercised instead by Stage 14's
/// manual smoke test against the real running app.
/// </summary>
public sealed class WebSessionTests
{
    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    private static SessionContext MakeContext(string host, int port = 0) => new()
    {
        SessionId = Guid.NewGuid(),
        Profile = new ConnectionProfile { Name = "test-web", ProtocolId = "https", Host = host, Port = port },
        HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
    };

    [Fact]
    public async Task ConnectAsync_Default_ExposesAWebChannel_WithTheExpectedUrl()
    {
        var ctx = MakeContext("example.com");
        var session = new WebSession(ctx, "https", NullLogger<WebSession>.Instance);

        await session.ConnectAsync();

        session.State.Should().Be(SessionState.Connected);
        session.Web.Should().NotBeNull("the default behavior is now an embedded WebView tab, not an external browser launch");
        session.Web!.CurrentUri.Should().Be(new Uri("https://example.com"));
    }

    [Fact]
    public async Task ConnectAsync_Default_AppendsANonDefaultPort()
    {
        var ctx = MakeContext("example.com", port: 8443);
        var session = new WebSession(ctx, "https", NullLogger<WebSession>.Instance);

        await session.ConnectAsync();

        session.Web!.CurrentUri.Should().Be(new Uri("https://example.com:8443"));
    }

}
