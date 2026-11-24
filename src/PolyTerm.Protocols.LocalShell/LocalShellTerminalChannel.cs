using PolyTerm.Engine;
using PolyTerm.Protocols.LocalShell.Platform;

namespace PolyTerm.Protocols.LocalShell;

/// <summary>
/// Terminal channel backed by an <see cref="IPseudoTerminal"/> (ConPTY or Unix PTY). The read loop is started
/// lazily on first <see cref="OutputReceived"/> subscription, so no bytes (including the initial shell prompt)
/// are lost while the terminal surface is still wiring up.
/// </summary>
internal sealed class LocalShellTerminalChannel : ITerminalChannel, IDisposable
{
    private readonly IPseudoTerminal _pty;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Passive sinks registered via RegisterOutputSink — fired from ReadLoop alongside OutputReceived,
    // but their registration does NOT start the lazy read loop (unlike OutputReceived.add).
    private Action<ReadOnlyMemory<byte>>? _sinks;

    public string TerminalType => "xterm-256color";

    // Lazy: ReadLoop starts on first OutputReceived subscription so the surface
    // is ready before bytes flow, preventing lost initial shell prompt.
    private EventHandler<ReadOnlyMemory<byte>>? _outputReceived;
    private Task? _readTask;

    /// <summary>
    /// Subscribing for the first time starts the background read loop (see <see cref="ReadLoop"/>); the loop
    /// keeps running for the lifetime of the channel even if all subscribers later unsubscribe.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add    { _outputReceived += value; _readTask ??= Task.Run(ReadLoop); }
        remove => _outputReceived -= value;
    }

    /// <summary>
    /// Registers a passive sink that receives the same byte chunks as <see cref="OutputReceived"/>, without
    /// starting the read loop — use this to attach a logger/recorder before the terminal surface subscribes.
    /// </summary>
    public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sinks += sink;

    public LocalShellTerminalChannel(IPseudoTerminal pty) => _pty = pty;

    public async ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _pty.Input.WriteAsync(data, ct).ConfigureAwait(false);
        await _pty.Input.FlushAsync(ct).ConfigureAwait(false);
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        _pty.Resize(cols, rows);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Continuously reads from the pseudo-terminal's output stream and fans each chunk out to
    /// <see cref="OutputReceived"/> subscribers and registered sinks until cancelled or the process exits.
    /// </summary>
    private async Task ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _pty.Output.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                var chunk = new ReadOnlyMemory<byte>(buf, 0, read);
                _outputReceived?.Invoke(this, chunk);
                _sinks?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* process exited */ }
    }

    /// <summary>
    /// Waits for the read loop to reach end of output, up to <paramref name="timeout"/>, so the shell's
    /// last bytes (a <c>logout</c> line, say) reach the terminal before the channel is disposed. Returns
    /// straight away if nothing ever subscribed. Bounded because a background job that inherited the
    /// PTY can keep it open after the shell itself has exited.
    /// </summary>
    internal async Task WaitForOutputDrainedAsync(TimeSpan timeout)
    {
        if (_readTask is not { } readTask) return;
        await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
    }

    /// <summary>Stops the read loop and disposes the underlying pseudo-terminal (killing the shell process). Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _pty.Dispose();
    }
}
