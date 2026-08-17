using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PolyTerm.Cli;

/// <summary>
/// Named-pipe JSON-RPC client for <c>polyterm attach</c>, talking to the same
/// <c>McpBridgeService</c> pipe PolyTerm.Mcp's <c>PolyTermClient</c> already uses (issue #114/#116) —
/// a separate small copy rather than a shared library reference, since PolyTerm.Mcp is a standalone
/// exe with its own entry point. Confirmed cross-platform directly on a real Linux box (issue #116):
/// <see cref="NamedPipeServerStream"/>/<see cref="NamedPipeClientStream"/> work unmodified there,
/// backed transparently by a Unix domain socket — no platform branching needed here.
/// </summary>
internal sealed class AttachPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    // Serializes CallAsync so only one request/response round-trip is ever in flight on the wire.
    // AttachToTerminalSessionAsync drives this client from two concurrent loops sharing one instance —
    // its own poll loop (attach_read) and a background stdin-forwarding loop (attach_send_input, fired
    // whenever real stdin has bytes). Without this lock, two concurrent CallAsync calls interleave
    // writes and race on the same StreamReader for ReadLineAsync, which can deadlock both sides
    // permanently waiting on the pipe (confirmed via a hang dump: both ends blocked in
    // StreamWriter.FlushAsyncInternal simultaneously) rather than just corrupting a response.
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public AttachPipeClient(string pipeName) =>
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    /// <summary>Connects to the named pipe and initializes the line-based reader/writer. Must be called before <see cref="CallAsync"/>.</summary>
    public async Task ConnectAsync(int timeoutMs, CancellationToken ct)
    {
        await _pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

        // UTF8Encoding(false) — no BOM. See McpBridgeService/PolyTermClient's matching comment: the
        // shared Encoding.UTF8 singleton's BOM preamble hangs the first Flush over a pipe with
        // nothing yet reading.
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _reader = new StreamReader(_pipe, noBomUtf8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, noBomUtf8, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Sends a JSON-RPC request and returns the <c>result</c> element. Throws if the bridge returns an error.</summary>
    public async Task<JsonElement> CallAsync(string method, object? args, CancellationToken ct)
    {
        if (_reader is null || _writer is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id  = Interlocked.Increment(ref _nextId);
            var req = JsonSerializer.Serialize(new { id, method, @params = args ?? new { } });

            await _writer.WriteLineAsync(req.AsMemory(), ct).ConfigureAwait(false);

            while (true)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("PolyTerm closed the pipe.");

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id)
                    continue; // stale response from a different request

                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(err.GetString() ?? "Unknown bridge error.");

                return root.GetProperty("result").Clone();
            }
        }
        finally
        {
            _callLock.Release();
        }
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _callLock.Dispose();
    }
}
