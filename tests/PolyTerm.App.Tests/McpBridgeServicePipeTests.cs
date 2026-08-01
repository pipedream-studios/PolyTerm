using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolyTerm.App.McpBridge;
using PolyTerm.App.McpBridge.Approval;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Regression test for a real, previously-shipped bug: the named-pipe MCP bridge never completed a
/// round trip at all — the first write from either side (client or server) hung indefinitely,
/// because both used the shared <c>Encoding.UTF8</c> singleton (which has a 3-byte BOM preamble)
/// for their <see cref="StreamWriter"/>s. <c>StreamWriter</c> writes that preamble on its first
/// <c>Flush()</c> — with <c>AutoFlush = true</c> set in the object initializer that's immediate — and
/// that write blocked forever with nothing yet reading from the pipe. Confirmed via a real client
/// connecting to a real running <c>PolyTerm.App</c> (captured in the server's own exception stack
/// trace once the hung client was killed: blocked inside <c>StreamWriter.Flush</c>, called from
/// <c>McpBridgeService.HandleConnectionAsync</c>'s <c>AutoFlush = true</c> initializer) — reproduced
/// on completely unmodified code, so this predates and is unrelated to the connection_open/
/// approval-gate work built alongside it. Fixed by using <c>new UTF8Encoding(false)</c> (no BOM) in
/// both <c>McpBridgeService</c> and <c>PolyTermClient</c>.
/// <para/>
/// This test talks to a real <see cref="McpBridgeService"/> instance over a real named pipe (a
/// private, per-test pipe name so it can't conflict with a live app) — not mocks for the pipe itself
/// — since the bug was specifically about real OS-level pipe I/O behavior that a mock would never
/// have caught in the first place. Every request routed through the real <c>HandleRequestAsync</c>
/// reads <c>App.Settings.McpMaxCallsPerMinute</c> for rate limiting, so this class is in the
/// <c>AppSettings</c> xUnit collection too (see <see cref="AppSettingsTestCollection"/>).
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServicePipeTests
{
    [Fact]
    public async Task RoundTrip_CompletesWithoutHanging()
    {
        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners now use a
        // $TMPDIR long enough that 'CoreFxPipe_polyterm-test-<32 hex chars>' pushed the full Unix
        // domain socket path past .NET's 104-byte UnixDomainSocketEndPoint limit, failing with
        // ArgumentOutOfRangeException before the pipe ever connected - confirmed the hard way in CI.
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];

        var sessions = Mock.Of<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());

        using var service = new McpBridgeService(sessions, profiles.Object, Mock.Of<IApprovalGate>(), NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();

        // Give the pipe server a moment to reach WaitForConnectionAsync.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var reader = new StreamReader(client, noBomUtf8, leaveOpen: true);
        var writer = new StreamWriter(client, noBomUtf8, leaveOpen: true) { AutoFlush = true };

        var request = JsonSerializer.Serialize(new { id = 1, method = "connection_list", @params = (object?)null });

        // The actual regression check: this used to hang forever (this Task.WhenAny is the safety
        // net that turns "test hangs forever" into "test fails with a clear message" if it ever
        // regresses, rather than blocking the whole test run).
        var writeTask = writer.WriteLineAsync(request);
        var writeCompleted = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        writeCompleted.Should().Be(writeTask, "writing a request to the pipe should never hang");

        var readTask = reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
        var readCompleted = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        readCompleted.Should().Be(readTask, "reading the response from the pipe should never hang");

        var responseLine = await (Task<string?>)readTask;
        responseLine.Should().NotBeNull();

        using var doc = JsonDocument.Parse(responseLine!);
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("result").GetProperty("connections").GetArrayLength().Should().Be(0);

        service.Stop();
    }

    /// <summary>
    /// Regression test for the pipe's previous one-client-at-a-time limitation
    /// (<c>maxNumberOfServerInstances: 1</c>, one accept loop). Deliberately holds the first client's
    /// connection open and idle (no round trip, no disconnect) while a second client attempts to
    /// connect with a short timeout — with the old single-instance behavior, no second listening pipe
    /// instance exists until the first client goes away, so the second connect would have to wait for
    /// (and here, time out before) that; with real concurrent instances, it succeeds immediately.
    /// A prior version of this test only overlapped fast round trips end-to-end, which passed even
    /// with just one accept loop (client B's connect-retry easily outraced the assertion window once
    /// client A's short-lived connection freed up) — not a real regression check. This one only
    /// passes if a second pipe instance was genuinely listening while the first client was still
    /// connected.
    /// </summary>
    [Fact]
    public async Task MultipleClients_CanBeConnectedSimultaneously()
    {
        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners now use a
        // $TMPDIR long enough that 'CoreFxPipe_polyterm-test-<32 hex chars>' pushed the full Unix
        // domain socket path past .NET's 104-byte UnixDomainSocketEndPoint limit, failing with
        // ArgumentOutOfRangeException before the pipe ever connected - confirmed the hard way in CI.
        var pipeName = $"pt-{Guid.NewGuid():N}"[..15];

        var sessions = Mock.Of<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());

        using var service = new McpBridgeService(sessions, profiles.Object, Mock.Of<IApprovalGate>(), NullLogger<McpBridgeService>.Instance, pipeName);
        service.Start();

        await Task.Delay(200, TestContext.Current.CancellationToken);

        using var clientA = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await clientA.ConnectAsync(ctsA.Token);
        clientA.IsConnected.Should().BeTrue("client A should be connected and left open, deliberately not disconnecting");

        using var clientB = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var ctsB = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Func<Task> connectB = async () => await clientB.ConnectAsync(ctsB.Token);

        await connectB.Should().NotThrowAsync(
            "a second server pipe instance should already be listening while client A is still connected, " +
            "not only after it disconnects");

        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Now that both are confirmed connected simultaneously, exercise a real round trip on each.
        async Task<int> RoundTrip(NamedPipeClientStream client, int id)
        {
            var reader = new StreamReader(client, noBomUtf8, leaveOpen: true);
            var writer = new StreamWriter(client, noBomUtf8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method = "connection_list", @params = (object?)null }));
            var responseLine = await reader.ReadLineAsync();
            responseLine.Should().NotBeNull();
            using var doc = JsonDocument.Parse(responseLine!);
            return doc.RootElement.GetProperty("id").GetInt32();
        }

        (await RoundTrip(clientA, 1)).Should().Be(1);
        (await RoundTrip(clientB, 2)).Should().Be(2);

        service.Stop();
    }
}
