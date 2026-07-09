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
    private bool _disposed;

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

    public SerialTerminalChannel(SerialPort port) => _port = port;

    public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        _port.BaseStream.WriteAsync(data, ct);

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
        catch (OperationCanceledException) { }
        catch (Exception) { /* port closed/error */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
    }
}
