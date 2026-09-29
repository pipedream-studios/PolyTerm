using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Mcp;

/// <summary>
/// Named-pipe JSON-RPC client that talks to the McpBridgeService inside PolyTerm.App.
/// Each call is sequential — no concurrent requests.
/// </summary>
/// <remarks>
/// Connects lazily on the first call rather than at startup, so an MCP host (Claude Desktop, Claude
/// Code) can launch <c>polyterm-mcp</c> before PolyTerm itself is running — the handshake and
/// <c>tools/list</c> never touch the pipe, and a tool call made while PolyTerm is down fails with a
/// clear error instead of the whole server exiting. If PolyTerm restarts, the next call reconnects.
/// </remarks>
internal sealed class PolyTermClient : IPolyTermClient, IDisposable
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public PolyTermClient(string pipeName, int connectTimeoutMs = 5_000)
    {
        _pipeName = pipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    /// <summary>
    /// Sends a JSON-RPC request and returns the <c>result</c> element, connecting first if needed.
    /// Throws if PolyTerm isn't reachable or the bridge returns an error.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken ct = default)
    {
        var id  = Interlocked.Increment(ref _nextId);
        var req = JsonSerializer.Serialize(new { id, method, @params = args ?? new { } });

        await SendAsync(req, ct).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false)
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Not retried: the request was already sent, and it may have been a state-changing tool
            // that ran before PolyTerm went away. Drop the dead pipe so the next call reconnects.
            Disconnect();
            throw new InvalidOperationException($"Lost connection to PolyTerm during '{method}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes one request line, connecting first if needed. A write that fails on a stale pipe (PolyTerm
    /// restarted since the last call) never reached PolyTerm, so it's safe to reconnect and retry once.
    /// </summary>
    private async Task SendAsync(string request, CancellationToken ct)
    {
        var wasConnected = _writer is not null;
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer!.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (wasConnected && ex is IOException or ObjectDisposedException)
        {
            Disconnect();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await _writer!.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;
        Disconnect();

        // A NamedPipeClientStream can't be reconnected once broken — always start from a fresh one.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(_connectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new InvalidOperationException(
                $"PolyTerm isn't running (no response on pipe '{_pipeName}'). Start PolyTerm and try again.");
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        // UTF8Encoding(false) — no BOM. Encoding.UTF8 (the shared singleton) has a 3-byte preamble
        // that StreamWriter writes on its first Flush; over a named pipe with nothing reading yet,
        // that write blocks indefinitely (confirmed: the exact hang this client had — see docs/todos.md).
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _pipe   = pipe;
        _reader = new StreamReader(pipe, noBomUtf8, leaveOpen: true);
        _writer = new StreamWriter(pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };
    }

    private void Disconnect()
    {
        // The writer's Dispose flushes, which throws on an already-broken pipe — nothing left to save.
        try { _writer?.Dispose(); } catch (IOException) { }
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe   = null;
    }

    /// <summary>Closes the underlying named pipe, if connected.</summary>
    public void Dispose() => Disconnect();
}
