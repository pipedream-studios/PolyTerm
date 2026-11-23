using System.Text.Json;
using AwesomeAssertions;
using PolyTerm.Cli;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>
/// Covers <see cref="AttachPipeClient"/> — the JSON-RPC-over-named-pipe framing <c>polyterm attach</c>
/// uses to talk to a running PolyTerm.App's <c>McpBridgeService</c> (issue #114/#116). This project has
/// no reference to PolyTerm.App, so unlike <c>McpBridgeServiceAttachTests</c> (which exercises the real
/// server against this same wire protocol), these tests run a small hand-rolled fake server on a real
/// named pipe — enough to prove <see cref="AttachPipeClient"/>'s own request/response handling
/// (framing, id-matching, error surfacing), not the bridge's business logic.
/// </summary>
public sealed class AttachPipeClientTests : IAsyncDisposable
{
    private readonly List<FakePipeServer> _servers = [];

    /// <summary>Disposes every fake server this test started, so none outlives the test host (issue #226). xunit v3 awaits IAsyncDisposable on test classes after each test.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    /// <summary>Starts a fake single-shot pipe server that replies to each request using <paramref name="respond"/>, mirroring McpBridgeService's own no-BOM UTF-8 line framing.</summary>
    private Task<(string PipeName, FakePipeServer Server)> StartFakeServerAsync(
        Func<JsonElement, int, string> respond, CancellationToken ct)
    {
        var server = FakePipeServer.Start(root =>
        {
            var id = root.GetProperty("id").GetInt32();
            root.TryGetProperty("params", out var @params);
            return respond(@params, id);
        }, ct);
        _servers.Add(server);
        return Task.FromResult((server.PipeName, server));
    }

    [Fact]
    public async Task CallAsync_RoundTrips_AndReturnsTheResultElement()
    {
        var ct = CancellationToken.None;
        var (pipeName, _) = await StartFakeServerAsync(
            (_, id) => JsonSerializer.Serialize(new { id, result = new { text = "hello", total_lines = 1 } }), ct);

        using var client = new AttachPipeClient(pipeName);
        await client.ConnectAsync(5_000, ct);

        var result = await client.CallAsync("attach_read", new { session_id = "abc", since = 0 }, ct);

        result.GetProperty("text").GetString().Should().Be("hello");
        result.GetProperty("total_lines").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task CallAsync_WhenServerReturnsAnErrorField_ThrowsWithThatMessage()
    {
        var ct = CancellationToken.None;
        var (pipeName, _) = await StartFakeServerAsync(
            (_, id) => JsonSerializer.Serialize(new { id, error = "Session 'abc' is already attached from another client." }), ct);

        using var client = new AttachPipeClient(pipeName);
        await client.ConnectAsync(5_000, ct);

        var act = () => client.CallAsync("attach_start", new { session_id = "abc" }, ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already attached*");
    }

    [Fact]
    public async Task CallAsync_SendsTheMethodAndParamsTheServerActuallyReceives()
    {
        var ct = CancellationToken.None;
        JsonElement receivedParams = default;
        var (pipeName, _) = await StartFakeServerAsync(
            (@params, id) => { receivedParams = @params.Clone(); return JsonSerializer.Serialize(new { id, result = new { ok = true } }); }, ct);

        using var client = new AttachPipeClient(pipeName);
        await client.ConnectAsync(5_000, ct);

        await client.CallAsync("attach_send_input", new { session_id = "abc", data = "ls\n" }, ct);

        receivedParams.GetProperty("session_id").GetString().Should().Be("abc");
        receivedParams.GetProperty("data").GetString().Should().Be("ls\n");
    }
}
