using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Minimal raw client for <c>McpBridgeService</c>'s named-pipe JSON-RPC-style protocol - a
/// newline-delimited JSON request per call (<c>{"id", "method", "params"}</c>), a newline-delimited
/// JSON response per reply (<c>{"id", "result"}</c> or <c>{"id", "error"}</c>). Not
/// <c>PolyTerm.Mcp.exe</c> itself (that's a separate stdio-to-pipe bridge for real MCP clients like
/// Claude Desktop) - just enough of the wire protocol to drive a real tool call from a test, the same
/// way any real MCP client would, to exercise <c>ApprovalGateService</c>'s real
/// <c>ConfirmDialog</c> for real.
/// </summary>
internal sealed class McpTestClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _nextId = 1;

    private McpTestClient(NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer)
    {
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public static async Task<McpTestClient> ConnectAsync(string pipeName, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);

        // No-BOM UTF8, matching McpBridgeService's own encoding choice (a BOM-emitting first flush
        // over a pipe with nothing reading yet blocks indefinitely - see that class's own comment).
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var reader = new StreamReader(pipe, noBomUtf8);
        var writer = new StreamWriter(pipe, noBomUtf8) { AutoFlush = true };
        return new McpTestClient(pipe, reader, writer);
    }

    /// <summary>Calls a tool by method name (e.g. "terminal_execute") with the given params object,
    /// returning the parsed "result" JsonElement. Throws if the response carries an "error" instead -
    /// callers that expect an approval denial should catch <see cref="InvalidOperationException"/>.</summary>
    public async Task<JsonElement> CallAsync(string method, object @params, CancellationToken ct = default)
    {
        var id = _nextId++;
        var request = JsonSerializer.Serialize(new { id, method, @params });
        await _writer.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);

        var responseLine = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MCP pipe closed before a response arrived.");

        using var doc = JsonDocument.Parse(responseLine);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MCP call '{method}' returned an error: {error.GetString()}");

        return doc.RootElement.GetProperty("result").Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
