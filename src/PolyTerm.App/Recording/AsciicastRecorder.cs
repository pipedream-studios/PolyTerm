using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PolyTerm.Engine;

namespace PolyTerm.App.Recording;

/// <summary>
/// Records terminal output in the asciicast v2 format (https://docs.asciinema.org/manual/asciicast/v2/).
/// The resulting .cast file can be played back with <c>asciinema play</c> or embedded on asciinema.org.
/// Survives an SSH auto-reconnect swapping in a new channel via <see cref="Rebind"/> — the file,
/// writer, and elapsed-time clock all continue rather than restarting.
/// </summary>
internal sealed class AsciicastRecorder : ISessionRecorder
{
    private ITerminalChannel _channel;
    private readonly StreamWriter _writer;
    private readonly Stopwatch _timer;
    private bool _disposed;
    private volatile bool _paused;

    /// <inheritdoc/>
    public bool IsPaused => _paused;

    /// <summary>Opens <paramref name="path"/> (truncating any existing file) and writes the asciicast v2 header, then starts tailing <paramref name="channel"/>'s output.</summary>
    public AsciicastRecorder(string path, ITerminalChannel channel, int cols, int rows, string title)
    {
        _channel = channel;
        _timer   = Stopwatch.StartNew();

        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            Encoding.UTF8, leaveOpen: false);

        // asciicast v2 header — one JSON object on line 1
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            version   = 2,
            width     = cols,
            height    = rows,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            title     = title,
        }));
        _writer.Flush();

        _channel.OutputReceived += OnOutput;
    }

    /// <summary>Appends one asciicast "o" (output) event with the elapsed time since recording started. No-ops while <see cref="IsPaused"/> — the elapsed-time clock keeps running regardless, so later events still land at the right timestamp.</summary>
    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_disposed || _paused) return;
        try
        {
            // asciicast event: [elapsed_seconds, "o", "data_as_utf8_string"]
            var elapsed = _timer.Elapsed.TotalSeconds;
            var text    = Encoding.UTF8.GetString(data.Span);
            _writer.Write('[');
            _writer.Write(elapsed.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            _writer.Write(",\"o\",");
            _writer.Write(JsonSerializer.Serialize(text));
            _writer.WriteLine(']');
            _writer.Flush();
        }
        catch { /* stop silently on I/O error */ }
    }

    /// <summary>Unsubscribes from the current channel and subscribes to <paramref name="channel"/> instead — the file and elapsed-time clock keep running, so playback timing stays accurate across the gap.</summary>
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

    /// <summary>Stops the timer, unsubscribes from the channel, and closes the file.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _channel.OutputReceived -= OnOutput;
        _writer.Dispose();
    }
}
