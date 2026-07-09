using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Mcp;

/// <summary>
/// Named-pipe JSON-RPC client that talks to the McpBridgeService inside PolyTerm.App.
/// Each call is sequential — no concurrent requests.
/// </summary>
internal sealed class PolyTermClient : IPolyTermClient, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public PolyTermClient(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    /// <summary>Connects to the named pipe (5 second timeout) and initializes the line-based reader/writer. Must be called before <see cref="CallAsync"/>.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _pipe.ConnectAsync(5_000, ct).ConfigureAwait(false);

        // UTF8Encoding(false) — no BOM. Encoding.UTF8 (the shared singleton) has a 3-byte preamble
        // that StreamWriter writes on its first Flush; over a named pipe with nothing reading yet,
        // that write blocks indefinitely (confirmed: the exact hang this client had — see docs/todos.md).
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(_pipe, noBomUtf8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>
    /// Sends a JSON-RPC request and returns the <c>result</c> element.
    /// Throws if the bridge returns an error.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken ct = default)
    {
        if (_reader is null || _writer is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var id  = Interlocked.Increment(ref _nextId);
        var req = JsonSerializer.Serialize(new { id, method, @params = args ?? new { } });

        await _writer.WriteLineAsync(req.AsMemory(), ct).ConfigureAwait(false);

        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new EndOfStreamException("PolyTerm closed the pipe.");

            // Clone before the document is disposed
            using var doc  = JsonDocument.Parse(line);
            var root       = doc.RootElement;

            if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id)
                continue; // stale response from a different request

            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString() ?? "Unknown bridge error.");

            return root.GetProperty("result").Clone();
        }
    }

    /// <summary>Closes the underlying named pipe.</summary>
    public void Dispose() => _pipe.Dispose();
}
