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
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _cancelGrace;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    // Set once a round trip was abandoned partway (its request was written but its response wasn't fully
    // read). The pipe is then out of step with the server, so every later call fails fast instead.
    private volatile bool _broken;

    /// <param name="pipeName">The bridge's pipe name.</param>
    /// <param name="responseTimeout">How long one round trip may take before the connection is given up on. Default 30 s.</param>
    /// <param name="cancelGrace">How long a round trip already on the wire keeps waiting for its response after the caller cancels. Default 5 s.</param>
    public AttachPipeClient(string pipeName, TimeSpan? responseTimeout = null, TimeSpan? cancelGrace = null)
    {
        _pipe            = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
        _cancelGrace     = cancelGrace ?? TimeSpan.FromSeconds(5);
    }

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

    /// <summary>
    /// Sends a JSON-RPC request and returns the <c>result</c> element. Throws if the bridge returns an error.
    /// <para>
    /// <paramref name="ct"/> stops a call that is still waiting its turn. Once the request is on the
    /// wire, cancelling does not drop the response: the call keeps reading for up to the cancel grace
    /// period and then throws <see cref="OperationCanceledException"/>, so the next call (such as
    /// <c>attach_stop</c> after Ctrl+C) starts on a clean pipe. Dropping it mid-read deadlocked on
    /// Windows (issue #321): the server stayed blocked writing the orphaned response while the client
    /// blocked writing its next request, and neither side read. If the response still doesn't arrive
    /// in time, the connection is marked broken and later calls throw <see cref="IOException"/> at once.
    /// </para>
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, object? args, CancellationToken ct)
    {
        if (_reader is null || _writer is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_broken)
                throw new IOException("The connection to PolyTerm was abandoned mid-call and can't be reused.");
            ct.ThrowIfCancellationRequested();

            var id  = Interlocked.Increment(ref _nextId);
            var req = JsonSerializer.Serialize(new { id, method, @params = args ?? new { } });

            // The round trip is bounded by its own timeout, not by ct directly. Cancelling ct only
            // shortens what's left to the grace period.
            using var roundTripCts = new CancellationTokenSource(_responseTimeout);
            using var onCancel     = ct.Register(() =>
            {
                try { roundTripCts.CancelAfter(_cancelGrace); }
                catch (ObjectDisposedException) { /* round trip already finished */ }
            });

            try
            {
                await _writer.WriteLineAsync(req.AsMemory(), roundTripCts.Token).ConfigureAwait(false);

                while (true)
                {
                    var line = await _reader.ReadLineAsync(roundTripCts.Token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("PolyTerm closed the pipe.");

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id)
                        continue; // stale response from a different request

                    if (root.TryGetProperty("error", out var err))
                        throw new InvalidOperationException(err.GetString() ?? "Unknown bridge error.");

                    var result = root.GetProperty("result").Clone();
                    // The response arrived, so the pipe is in step. Still honour a cancel that came
                    // in during the call, the same way a call that hadn't started yet would.
                    ct.ThrowIfCancellationRequested();
                    return result;
                }
            }
            catch (OperationCanceledException) when (roundTripCts.IsCancellationRequested)
            {
                _broken = true;
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException($"'{method}' was cancelled and its response didn't arrive within the grace period.", ct);
                throw new TimeoutException($"PolyTerm didn't answer '{method}' within {_responseTimeout.TotalSeconds:0.#} s.");
            }
            catch (IOException)
            {
                _broken = true;
                throw;
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
