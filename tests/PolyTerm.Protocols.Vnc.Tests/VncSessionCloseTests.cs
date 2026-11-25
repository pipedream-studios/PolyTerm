using System.Net.Sockets;
using AwesomeAssertions;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Protocols.Vnc.Tests;

/// <summary>
/// Closing a VNC session must complete, and must not take the process down (issue #374). With the
/// library's own background threads, every one of these either never completes or ends in an unhandled
/// exception on a raw thread. The second can't be caught by a test: it kills the test process, which
/// fails the whole run. So there's no assertion for it beyond the test still being alive.
/// </summary>
public sealed class VncSessionCloseTests
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    public enum ServerBehaviour
    {
        /// <summary>An idle desktop: the client's receive loop is waiting in a read when it's closed.</summary>
        SaysNothing,

        /// <summary>A busy desktop: framebuffer updates keep arriving while the connection closes.</summary>
        StreamsFrames,

        /// <summary>The same, with the smallest message there is (Bell), so more arrive during the close.</summary>
        RingsTheBell,
    }

    [Theory]
    [InlineData(ServerBehaviour.SaysNothing)]
    [InlineData(ServerBehaviour.StreamsFrames)]
    [InlineData(ServerBehaviour.RingsTheBell)]
    public async Task DisconnectAsync_ClosesTheConnection_AndTheProcessSurvives(ServerBehaviour behaviour)
    {
        var clientClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRfbServer(4, 4, (1, 2, 3))
        {
            AfterUpdate = async (stream, update, ct) =>
            {
                var drained = DrainUntilClosedAsync(stream, ct);
                byte[]? message = behaviour switch
                {
                    ServerBehaviour.StreamsFrames => update,
                    ServerBehaviour.RingsTheBell  => [2],
                    _                             => null,
                };
                try
                {
                    while (message is not null && !drained.IsCompleted)
                    {
                        await stream.WriteAsync(message, ct);
                        await Task.Delay(1, ct);
                    }
                }
                catch (IOException)
                {
                    // The client closed while we were writing to it.
                }
                await drained;
                clientClosed.TrySetResult();
            },
        };
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        server.Start(serverCts.Token);

        await using var session = await ConnectAndReceiveAFrameAsync(server);

        await session.DisconnectAsync(TestContext.Current.CancellationToken)
            .WaitAsync(CloseTimeout, TestContext.Current.CancellationToken);

        session.State.Should().Be(SessionState.Closed);
        await clientClosed.Task.WaitAsync(CloseTimeout, TestContext.Current.CancellationToken);
        await session.DisposeAsync();
        await GiveAnEscapedExceptionTimeToEndTheProcessAsync();
    }

    [Fact]
    public async Task DisconnectAsync_AfterTheServerDroppedTheConnection_Completes()
    {
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRfbServer(4, 4, (1, 2, 3))
        {
            AfterUpdate = async (stream, _, ct) =>
            {
                await drop.Task.WaitAsync(ct);
                stream.Socket.Shutdown(SocketShutdown.Send);
                await DrainUntilClosedAsync(stream, ct);
            },
        };
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        server.Start(serverCts.Token);

        await using var session = await ConnectAndReceiveAFrameAsync(server);
        var connection = ((VncFramebufferChannel)session.Framebuffer!).Connection!;
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RfbConnection.ConnectionState) && connection.ConnectionState != ConnectionState.Connected)
                interrupted.TrySetResult();
        };

        drop.SetResult();
        await interrupted.Task.WaitAsync(CloseTimeout, TestContext.Current.CancellationToken);

        // The library is now trying to reconnect; closing has to stop that too.
        await session.DisconnectAsync(TestContext.Current.CancellationToken)
            .WaitAsync(CloseTimeout, TestContext.Current.CancellationToken);

        session.State.Should().Be(SessionState.Closed);
        await session.DisposeAsync();
        await GiveAnEscapedExceptionTimeToEndTheProcessAsync();
    }

    private static async Task<VncSession> ConnectAndReceiveAFrameAsync(FakeRfbServer server)
    {
        var profile = new ConnectionProfile { Name = "fake-vnc", ProtocolId = "vnc", Host = "127.0.0.1", Port = server.Port };
        var session = new VncSession(
            new SessionContext { SessionId = Guid.NewGuid(), Profile = profile, HostKeyPolicy = new AlwaysTrustHostKeyPolicy() },
            NullLoggerFactory.Instance);

        await session.ConnectAsync(TestContext.Current.CancellationToken);

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Framebuffer!.RegionUpdated += (_, _) => received.TrySetResult();

        // Asked for explicitly, for the reason given in VncPixelFormatTests.
        ((VncFramebufferChannel)session.Framebuffer!).Connection!.EnqueueMessage(
            new FramebufferUpdateRequestMessage(incremental: false, new Rectangle(Position.Origin, new Size(4, 4))),
            TestContext.Current.CancellationToken);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        return session;
    }

    /// <summary>Reads and discards what the client sends, until it closes the connection.</summary>
    private static async Task DrainUntilClosedAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, ct) > 0)
            {
            }
        }
        catch (IOException)
        {
            // A reset counts as closed too: the client can close with our data still unread.
        }
    }

    /// <summary>
    /// The library's threads can still be unwinding after <c>DisconnectAsync</c> returns. Without a
    /// pause the run could finish before an exception escaping one of them brought it down.
    /// </summary>
    private static Task GiveAnEscapedExceptionTimeToEndTheProcessAsync() =>
        Task.Delay(500, TestContext.Current.CancellationToken);

    private sealed class AlwaysTrustHostKeyPolicy : IHostKeyPolicy
    {
        public Task<HostKeyDecision> EvaluateAsync(HostKeyInfo key, CancellationToken ct = default) =>
            Task.FromResult(HostKeyDecision.Trust);
    }
}
