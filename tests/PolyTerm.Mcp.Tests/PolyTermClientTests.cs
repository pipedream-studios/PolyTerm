using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.Mcp.Tests;

/// <summary>
/// Covers <see cref="PolyTermClient"/>'s lazy connect/reconnect over a real named pipe, standing in for
/// PolyTerm.App's McpBridgeService with a minimal echo server. This is what lets an MCP host (Claude
/// Desktop) start <c>polyterm-mcp</c> before PolyTerm is running, and survive PolyTerm restarting.
/// </summary>
public sealed class PolyTermClientTests
{
    // Kept short on purpose: on macOS/Linux a named pipe is a Unix domain socket at $TMPDIR/CoreFxPipe_<name>,
    // and macOS rejects paths over ~104 chars — a full GUID plus the runner's long $TMPDIR overflows that.
    private static string NewPipeName() => $"pt-{Guid.NewGuid():N}"[..11];

    /// <summary>
    /// Accepts one client and answers each request line with <c>{"id":..,"result":{"method":..}}</c>
    /// until the client disconnects or <paramref name="maxRequests"/> is reached, then closes the pipe.
    /// </summary>
    private static async Task ServeAsync(string pipeName, int maxRequests, CancellationToken ct)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(ct);

        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(server, utf8, leaveOpen: true);
        await using var writer = new StreamWriter(server, utf8, leaveOpen: true) { AutoFlush = true };

        for (var i = 0; i < maxRequests; i++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetInt32();
            var method = doc.RootElement.GetProperty("method").GetString();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { method } }));
        }
    }

    [Fact]
    public async Task CallAsync_WhenPolyTermNotRunning_ThrowsClearError()
    {
        var pipeName = NewPipeName();
        using var client = new PolyTermClient(pipeName, connectTimeoutMs: 200);

        var act = () => client.CallAsync("session_list");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*PolyTerm isn't running*");
    }

    [Fact]
    public async Task CallAsync_ConnectsLazily_OncePolyTermStarts()
    {
        var pipeName = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new PolyTermClient(pipeName, connectTimeoutMs: 200);

        // First call before the "app" is up fails but doesn't poison the client.
        await FluentActions.Awaiting(() => client.CallAsync("session_list", ct: cts.Token))
            .Should().ThrowAsync<InvalidOperationException>();

        var serve = ServeAsync(pipeName, maxRequests: 1, cts.Token);
        var result = await client.CallAsync("session_list", ct: cts.Token);

        result.GetProperty("method").GetString().Should().Be("session_list");
        await serve;
    }

    [Fact]
    public async Task CallAsync_AfterPolyTermRestarts_Reconnects()
    {
        var pipeName = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new PolyTermClient(pipeName, connectTimeoutMs: 2_000);

        // First "PolyTerm" instance answers one request, then exits (closes the pipe).
        var first = ServeAsync(pipeName, maxRequests: 1, cts.Token);
        (await client.CallAsync("session_list", ct: cts.Token)).GetProperty("method").GetString().Should().Be("session_list");
        await first;

        // A restarted instance on the same pipe name; the client's old pipe is now broken.
        var second = ServeAsync(pipeName, maxRequests: 1, cts.Token);
        var result = await client.CallAsync("connection_list", ct: cts.Token);

        result.GetProperty("method").GetString().Should().Be("connection_list");
        await second;
    }
}
