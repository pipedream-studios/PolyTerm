using PolyTerm.Engine;

namespace PolyTerm.App.Recording;

/// <summary>
/// Common surface for <see cref="AsciicastRecorder"/> and <see cref="RawSessionRecorder"/>: both tail
/// an <see cref="ITerminalChannel"/>'s output to a file and can be re-pointed at a fresh channel
/// (e.g. after an SSH auto-reconnect swaps in a new one) without losing the file, writer, or timer.
/// </summary>
internal interface ISessionRecorder : IDisposable
{
    /// <summary>Unsubscribes from the current channel and subscribes to <paramref name="channel"/> instead. The output file continues rather than restarting.</summary>
    void Rebind(ITerminalChannel channel);

    /// <summary>True after <see cref="Pause"/> until the next <see cref="Resume"/>.</summary>
    bool IsPaused { get; }

    /// <summary>Stops writing output to the file until <see cref="Resume"/> — e.g. before the user runs a sensitive command. The asciicast elapsed-time clock (if any) keeps running, so playback timing across the gap stays accurate; there's just a gap in the output.</summary>
    void Pause();

    /// <summary>Resumes writing output to the file after <see cref="Pause"/>.</summary>
    void Resume();
}
