namespace PolyTerm.Protocols.LocalShell.Platform;

/// <summary>
/// Abstraction over a platform pseudo-terminal hosting a shell process — Windows ConPTY
/// (<see cref="ConPtyTerminal"/>) or Unix openpty (<see cref="UnixPtyTerminal"/>).
/// </summary>
internal interface IPseudoTerminal : IDisposable
{
    /// <summary>Stream to write terminal input (keystrokes) to the shell.</summary>
    Stream Input { get; }

    /// <summary>Stream to read terminal output (screen bytes) from the shell.</summary>
    Stream Output { get; }

    /// <summary>Completes when the shell process exits.</summary>
    Task WaitForExitAsync(CancellationToken ct);

    /// <summary>Notifies the shell of a terminal window size change.</summary>
    void Resize(int cols, int rows);
}
