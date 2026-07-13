using AwesomeAssertions;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Vnc.Tests;

/// <summary>
/// End-to-end against a hand-rolled fake RFB server (<see cref="FakeRfbServer"/>) — connects a real
/// <see cref="VncSession"/> through the real <c>MarcusW.VncClient</c> library, and inspects the actual
/// decoded bytes <see cref="IFramebufferChannel.RegionUpdated"/> delivers. This is the one test in the
/// whole VNC module that exercises real wire-level pixel-format conversion rather than just PolyTerm's
/// own buffer bookkeeping — see docs/todos.md's VNC section for why this was flagged as unverified
/// (RGBA vs BGRA byte order) rather than just asserted from reading source/docs.
/// </summary>
public sealed class VncPixelFormatTests
{
    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        // VncSession never calls this — VNC has no host-key verification step — but SessionContext
        // requires a policy instance to construct.
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }

    [Fact]
    public async Task RegionUpdated_DeliversPixelBytes_MatchingKnownServerInput()
    {
        // Distinct, unambiguous per-channel values — if any channel ends up in the wrong byte
        // position (or alpha lands somewhere it shouldn't), this makes it obvious which.
        const byte r = 0x10, g = 0x20, b = 0x30;
        const int width = 4, height = 4;

        var server = new FakeRfbServer(width, height, (r, g, b));
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        server.Start(serverCts.Token);

        var profile = new ConnectionProfile
        {
            Name = "fake-vnc", ProtocolId = "vnc", Host = "127.0.0.1", Port = server.Port,
        };
        var ctx = new SessionContext
        {
            SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new AlwaysTrustHostKeyPolicy(),
        };

        // Deliberately CancellationToken.None for ConnectAsync/the session's own lifetime — MarcusW.VncClient's
        // internal background threads (RfbMessageSender etc.) appear to key off whatever token flows into
        // ConnectAsync, and their thread-shutdown path throws an uncaught OperationCanceledException
        // (crashing the whole process, not just failing the test) once that token is cancelled. This test
        // deliberately never cancels or disposes the session/connection at all, to avoid tripping that path —
        // a real, alpha-quality-library limitation, not something to route around by suppressing exceptions.
        var session = new VncSession(ctx, NullLoggerFactory.Instance);

        var updateReceived = new TaskCompletionSource<FramebufferRegionUpdatedEventArgs>();
        await session.ConnectAsync(CancellationToken.None);

        session.Framebuffer.Should().NotBeNull("VncSession should expose a Framebuffer channel once connected");
        session.Framebuffer!.RegionUpdated += (_, e) => updateReceived.TrySetResult(e);

        // Explicitly request an update instead of waiting on MarcusW.VncClient's own automatic
        // scheduling of the first request. Confirmed by direct observation this varies wildly and
        // isn't just "CI is slow": the exact same code/server passed in 239ms on one CI run, then timed
        // out at first 8s and later even 25s on later runs with zero VNC code changes in between —
        // three wildly different orders of magnitude points at the library's own internal timing
        // (likely some capability-negotiation retry/backoff our minimal FakeRfbServer never
        // participates in), not runner speed. FakeRfbServer already sends its crafted update as soon
        // as it receives *any* bytes from the client (see its own comment) — sending this ourselves
        // makes that happen immediately and deterministically, rather than at the mercy of whatever
        // the library decides to do first.
        var connection = ((VncFramebufferChannel)session.Framebuffer!).Connection
            ?? throw new InvalidOperationException("Connection should be set once VncSession.ConnectAsync has returned.");
        connection.EnqueueMessage(
            new FramebufferUpdateRequestMessage(incremental: false, new Rectangle(Position.Origin, new Size(width, height))),
            CancellationToken.None);

        // The explicit-request fix above made this fast and deterministic locally, and for a while on
        // ubuntu-latest's portable-subset CI leg too, but it went on to genuinely hang on all three CI
        // legs (always failing at exactly whatever timeout was configured, never varying - a real
        // hang, not slower hardware) - see issue #22's full history. Root cause: FakeRfbServer's own
        // post-handshake drain step (see its updated comment) used a single one-shot ReadAsync,
        // assuming the client's whole SetEncodings/FramebufferUpdateRequest burst would already be
        // sitting in the socket buffer by the time it ran. MarcusW.VncClient's RfbMessageSender writes
        // each queued message as a separate call, so under enough scheduling delay for those later
        // writes - exactly what a shared, contended CI runner has and an idle local dev box doesn't -
        // the drain could read only the first fragment and reply prematurely, before the client had
        // even finished sending its own handshake tail. Fixed by settling before proceeding instead of
        // racing whichever fragment arrives first. This timeout is left at 20s regardless, as a
        // reasonable upper bound rather than because anything here still expects to need it.
        var completed = await Task.WhenAny(updateReceived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        completed.Should().Be(updateReceived.Task, "the fake server should have sent a FramebufferUpdate by now");

        var update = await updateReceived.Task;
        update.Width.Should().Be(width);
        update.Height.Should().Be(height);
        update.Data.Length.Should().Be(width * height * 4);

        // First pixel's 4 bytes, in whatever order PolyTerm actually delivers them.
        var firstPixel = update.Data.Span[..4].ToArray();

        // FramebufferSurface blits this data assuming Avalonia's Rgba8888 memory-byte-order
        // convention: byte0=R, byte1=G, byte2=B, byte3=A. Asserting that expectation directly
        // against the real, library-decoded bytes — if this fails, the actual bytes printed in the
        // assertion failure message show the real order, which is the point of this test.
        firstPixel.Should().Equal([r, g, b, 255],
            "FramebufferSurface expects byte0=R,byte1=G,byte2=B,byte3=A (Avalonia Rgba8888 order)");

        // CaptureSnapshot (added for the MCP framebuffer_screenshot tool) is a pull — verify it
        // returns the same real, live-decoded pixel data the RegionUpdated push event just delivered,
        // through the same real fake-server pipeline as the rest of this test, not a separate mock.
        var snapshot = session.Framebuffer!.CaptureSnapshot();
        snapshot.Should().NotBeNull("a frame has already been received by this point");
        snapshot.Should().Equal(update.Data.ToArray(), "CaptureSnapshot should reflect the same buffer RegionUpdated just delivered");
    }
}
