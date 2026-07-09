using PolyTerm.Engine;

namespace PolyTerm.App.Recording;

/// <summary>
/// Writes raw terminal bytes (VT sequences included) to a file.
/// Useful for debugging the emulator or piping to other tools.
/// Survives an SSH auto-reconnect swapping in a new channel via <see cref="Rebind"/>.
/// </summary>
internal sealed class RawSessionRecorder : ISessionRecorder
{
    private ITerminalChannel _channel;
    private readonly FileStream _file;
    private bool _disposed;
    private volatile bool _paused;

    /// <inheritdoc/>
    public bool IsPaused => _paused;

    /// <summary>Opens <paramref name="path"/> (truncating any existing file) and starts tailing <paramref name="channel"/>'s raw output.</summary>
    public RawSessionRecorder(string path, ITerminalChannel channel)
    {
        _channel = channel;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.Read, bufferSize: 4096, useAsync: false);
        _channel.OutputReceived += OnOutput;
    }

    /// <summary>Writes the raw output bytes verbatim to the file. No-ops while <see cref="IsPaused"/>.</summary>
    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_disposed || _paused) return;
        try
        {
            _file.Write(data.Span);
            _file.Flush();
        }
        catch { /* stop silently on I/O error */ }
    }

    /// <summary>Unsubscribes from the current channel and subscribes to <paramref name="channel"/> instead — the file keeps writing.</summary>
    public void Rebind(ITerminalChannel channel)
    {
        if (_disposed) return;
        _channel.OutputReceived -= OnOutput;
        _channel = channel;
        _channel.OutputReceived += OnOutput;
    }

    /// <inheritdoc/>
    public void Pause() => _paused = true;

    /// <inheritdoc/>
    public void Resume() => _paused = false;

    /// <summary>Unsubscribes from the channel and closes the file.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.OutputReceived -= OnOutput;
        _file.Dispose();
    }
}
