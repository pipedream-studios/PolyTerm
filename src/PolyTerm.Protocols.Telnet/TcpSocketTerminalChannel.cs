using System.Net.Sockets;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Telnet;

/// <summary>
/// Byte-stream terminal channel over a connected TCP socket — shared by Telnet, raw, and rlogin,
/// which differ only in what (if anything) they do to bytes before they're written or after they're
/// read. Default behavior is pure passthrough (what "raw" needs exactly); <see cref="TelnetTerminalChannel"/>
/// overrides <see cref="ProcessIncoming"/> to strip/interpret IAC sequences. Lazy read loop on first
/// <see cref="OutputReceived"/> subscription, mirroring <c>LocalShellTerminalChannel</c>/SSH's own
/// channel so no bytes (including the initial login banner) are lost while the surface wires up.
/// </summary>
internal class TcpSocketTerminalChannel : ITerminalChannel, IDisposable
{
    private readonly Socket _socket;
    protected readonly NetworkStream Stream;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private Action<ReadOnlyMemory<byte>>? _sinks;
    private EventHandler<ReadOnlyMemory<byte>>? _outputReceived;
    private Task? _readTask;

    public string TerminalType { get; }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add    { _outputReceived += value; _readTask ??= Task.Run(ReadLoop); }
        remove => _outputReceived -= value;
    }

    public void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) => _sinks += sink;

    public TcpSocketTerminalChannel(Socket socket, string terminalType)
    {
        _socket = socket;
        Stream = new NetworkStream(socket, ownsSocket: true);
        TerminalType = terminalType;
    }

    public virtual ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        Stream.WriteAsync(data, ct);

    /// <summary>No terminal-dimension concept over a plain socket by default — Telnet overrides this to send NAWS.</summary>
    public virtual ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>Hook for subclasses to strip/interpret protocol framing before data reaches subscribers. Default: unchanged.</summary>
    protected virtual ReadOnlyMemory<byte> ProcessIncoming(ReadOnlyMemory<byte> data) => data;

    private async Task ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await Stream.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                if (read == 0) break; // remote closed
                var chunk = ProcessIncoming(new ReadOnlyMemory<byte>(buf, 0, read));
                if (chunk.IsEmpty) continue;
                _outputReceived?.Invoke(this, chunk);
                _sinks?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* socket closed/error */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        Stream.Dispose();
        _socket.Dispose();
    }
}
