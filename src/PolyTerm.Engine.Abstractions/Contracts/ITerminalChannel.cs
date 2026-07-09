namespace PolyTerm.Engine;

/// <summary>
/// Byte-stream terminal channel. Events fire on engine threads — consumers must marshal to UI thread.
/// </summary>
public interface ITerminalChannel
{
    /// <summary>
    /// Raw output bytes from the remote/local process. In most implementations, subscribing for the
    /// first time lazily starts the underlying read loop — see <see cref="RegisterOutputSink"/> for a
    /// way to observe output without that side effect.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>> OutputReceived;

    /// <summary>Writes raw bytes (keystrokes, pasted text) to the remote/local process's input stream.</summary>
    ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Notifies the remote/local process of a terminal size change (e.g. SSH window-change).</summary>
    ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default);

    /// <summary>The `TERM` value this channel negotiated, e.g. "xterm-256color".</summary>
    string TerminalType { get; }

    /// <summary>
    /// Register a passive output sink for logging/monitoring without triggering the lazy read loop.
    /// The sink receives the same byte chunks as OutputReceived, from the read-loop thread.
    /// Default implementation is a no-op — override in concrete channels.
    /// </summary>
    void RegisterOutputSink(Action<ReadOnlyMemory<byte>> sink) { }
}
