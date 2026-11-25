using System.IO.Ports;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>
/// Terminal channel over an open <see cref="SerialPort"/>. Lazy read loop on first
/// <see cref="OutputReceived"/> subscription, same convention as every other channel in this repo
/// (SSH/local-shell/Telnet), so no bytes are lost while the surface wires up.
/// </summary>
internal sealed class SerialTerminalChannel : ITerminalChannel, IDisposable
{
    private readonly SerialPort _port;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    private Action<ReadOnlyMemory<byte>>? _sinks;
    private EventHandler<ReadOnlyMemory<byte>>? _outputReceived;
    private Task? _readTask;

    public string TerminalType => "dumb";

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add    { _outputReceived += value; _readTask ??= Task.Run(ReadLoop); }
        remove => _outputReceived -= value;
    }

    public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sinks += sink;

    /// <summary>
    /// Raised once, on the read loop's thread, when a read fails for a reason other than this channel
    /// being disposed: most often the adapter being unplugged, which on macOS fails the pending read
    /// with "Device not configured" (ENXIO) about a second after the cable is pulled (issue #91).
    /// </summary>
    public event EventHandler<Exception>? ReadFailed;

    /// <summary>Cancelled by <see cref="Dispose"/>. Taken from <see cref="_cts"/> up front, as the source is disposed with the channel.</summary>
    private readonly CancellationToken _closing;

    public SerialTerminalChannel(SerialPort port)
    {
        _port    = port;
        _closing = _cts.Token;
    }

    /// <summary>
    /// A write can stay pending for as long as flow control holds it off, so closing the channel cancels
    /// it: <see cref="SerialPort.Close"/> waits for every pending write (issue #342). A write ended that
    /// way, or made after the close, returns quietly.
    /// </summary>
    public async ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disposed) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing);
            await _port.BaseStream.WriteAsync(data, linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_disposed)
        {
            // Closed under us: the bytes are dropped along with the rest of the unsent output.
        }
    }

    /// <summary>No terminal-dimension concept over a serial line — the device on the other end has no way to be told.</summary>
    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;

    private async Task ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _port.BaseStream.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                if (read == 0) continue; // serial streams don't signal EOF the way sockets do
                _outputReceived?.Invoke(this, new ReadOnlyMemory<byte>(buf, 0, read));
                _sinks?.Invoke(new ReadOnlyMemory<byte>(buf, 0, read));
            }
        }
        catch (Exception ex) when (_disposed || ex is OperationCanceledException)
        {
            // Our own Dispose cancelled or closed the port: an expected end, not a failure.
        }
        catch (Exception ex)
        {
            ReadFailed?.Invoke(this, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        if (_port.IsOpen)
        {
            DiscardUnsentOutput();
            _port.Close();
        }
        _port.Dispose();
    }

    /// <summary>
    /// <see cref="SerialPort.Close"/> waits for all pending output to be sent first. A device holding
    /// us off with flow control may never let that happen, which would hang the close (issue #342).
    /// </summary>
    private void DiscardUnsentOutput()
    {
        try
        {
            _port.DiscardOutBuffer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The port is already gone (adapter unplugged), so there's nothing left to discard.
        }
    }
}
