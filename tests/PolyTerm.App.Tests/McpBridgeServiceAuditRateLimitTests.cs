using System.IO.Pipes;
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
/// Integration-level coverage for the rate limiter and audit logger actually wired into
/// <see cref="McpBridgeService"/>'s real request pipeline (not just their standalone unit tests) —
/// a real pipe connection, real (mocked-dependency) dispatch, and a real temp audit-log file. Mutates
/// <c>App.Settings.McpMaxCallsPerMinute</c>, so this class is in the <c>AppSettings</c> xUnit
/// collection (see <see cref="AppSettingsTestCollection"/>) along with everything else that touches it.
/// </summary>
[Collection("AppSettings")]
public sealed class McpBridgeServiceAuditRateLimitTests
{
    private static (McpBridgeService Service, string PipeName, string AuditPath) MakeService(int maxCallsPerMinute)
    {
        App.Settings.McpMaxCallsPerMinute = maxCallsPerMinute;

        // Short prefix + truncated GUID (not the full 32 hex chars) - macOS CI runners now use a
        // $TMPDIR long enough that 'CoreFxPipe_polyterm-...-<32 hex chars>' pushed the full Unix
        // domain socket path past .NET's 104-byte UnixDomainSocketEndPoint limit, failing with
        // ArgumentOutOfRangeException before the pipe ever connected - confirmed the hard way in CI.
        var pipeName  = $"pt-{Guid.NewGuid():N}"[..15];
        var auditPath = Path.Combine(Path.GetTempPath(), $"polyterm-test-audit-{Guid.NewGuid():N}.log");

        var sessions = Mock.Of<ISessionManager>();
        var profiles = new Mock<IProfileStore>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());

        var service = new McpBridgeService(sessions, profiles.Object, Mock.Of<IApprovalGate>(),
            NullLogger<McpBridgeService>.Instance, pipeName, auditPath);

        return (service, pipeName, auditPath);
    }

    private static async Task<(int Id, JsonElement Root)> RoundTrip(string pipeName, int id, string method = "connection_list")
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var reader = new StreamReader(client, noBomUtf8, leaveOpen: true);
        var writer = new StreamWriter(client, noBomUtf8, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = (object?)null }));
        var responseLine = await reader.ReadLineAsync();
        responseLine.Should().NotBeNull();

        using var doc = JsonDocument.Parse(responseLine!);
        return (doc.RootElement.GetProperty("id").GetInt32(), doc.RootElement.Clone());
    }

    [Fact]
    public async Task CallsWithinTheLimit_SucceedNormally()
    {
        var (service, pipeName, auditPath) = MakeService(maxCallsPerMinute: 100);
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);

            var (_, root) = await RoundTrip(pipeName, 1);
            root.TryGetProperty("error", out _).Should().BeFalse("a call within the limit should succeed, not error");
            root.GetProperty("result").GetProperty("connections").GetArrayLength().Should().Be(0);
        }
        finally { service.Stop(); service.Dispose(); File.Delete(auditPath); }
    }

    [Fact]
    public async Task CallOverTheLimit_ReturnsARateLimitError_NotACrash()
    {
        var (service, pipeName, auditPath) = MakeService(maxCallsPerMinute: 1);
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(connectCts.Token);

            var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var reader = new StreamReader(client, noBomUtf8, leaveOpen: true);
            var writer = new StreamWriter(client, noBomUtf8, leaveOpen: true) { AutoFlush = true };

            async Task<JsonElement> Send(int id)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method = "connection_list", @params = (object?)null }));
                var line = await reader.ReadLineAsync();
                using var doc = JsonDocument.Parse(line!);
                return doc.RootElement.Clone();
            }

            var first  = await Send(1);
            first.TryGetProperty("error", out _).Should().BeFalse("the first call is within the limit of 1");

            var second = await Send(2);
            second.TryGetProperty("error", out var errorEl).Should().BeTrue("the second call on the same connection exceeds the limit of 1/minute");
            errorEl.GetString().Should().Contain("Rate limit");
        }
        finally { service.Stop(); service.Dispose(); File.Delete(auditPath); App.Settings.McpMaxCallsPerMinute = 120; }
    }

    [Fact]
    public async Task EachCall_WritesAnAuditLogEntry()
    {
        var (service, pipeName, auditPath) = MakeService(maxCallsPerMinute: 100);
        try
        {
            service.Start();
            await Task.Delay(200, TestContext.Current.CancellationToken);

            await RoundTrip(pipeName, 1);
            await RoundTrip(pipeName, 2);

            // The audit write happens after the response is sent back over the pipe, so give it a
            // moment rather than racing the file write.
            await Task.Delay(200, TestContext.Current.CancellationToken);

            var lines = File.ReadAllLines(auditPath);
            lines.Should().HaveCount(2);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                doc.RootElement.GetProperty("tool").GetString().Should().Be("connection_list");
                doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            }
        }
        finally { service.Stop(); service.Dispose(); File.Delete(auditPath); }
    }
}
