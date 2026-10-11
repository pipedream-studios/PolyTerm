namespace PolyTerm.Terminal;

/// <summary>
/// Contract for terminal emulator implementations.
/// Future milestones will populate a user-facing emulator list from types that
/// implement this interface (VT100, xterm-256color, VT220, etc.).
/// </summary>
public interface ITerminalEmulator
{
    /// <summary>Current screen width in columns.</summary>
    int Cols { get; }

    /// <summary>Current screen height in rows.</summary>
    int Rows { get; }

    /// <summary>Cursor's current column (0-based).</summary>
    int CursorCol { get; }

    /// <summary>Cursor's current row (0-based).</summary>
    int CursorRow { get; }

    /// <summary>Whether the cursor should be rendered (DECTCEM, CSI ?25h/l).</summary>
    bool CursorVisible { get; }

    /// <summary>
    /// DECCKM (private mode 1) — when true, the four arrow keys should be encoded as application
    /// sequences (<c>ESC O A/B/C/D</c>) instead of normal-mode ones (<c>ESC [ A/B/C/D</c>). Consulted
    /// by the surface layer's key encoder, not by the emulator itself (which only ever receives bytes,
    /// never sends them).
    /// </summary>
    bool CursorKeyApplicationMode { get; }

    /// <summary>The current terminal title, as last set via OSC 0/1/2.</summary>
    string Title { get; }

    /// <summary>Number of lines currently held in the scrollback buffer.</summary>
    int ScrollbackLineCount { get; }

    /// <summary>Discards all scrollback lines; the visible screen is left untouched.</summary>
    void ClearScrollback();

    /// <summary>Fires whenever the visible screen changes.</summary>
    event EventHandler? ScreenUpdated;

    /// <summary>Fires when the terminal title changes (OSC 0/1/2).</summary>
    event EventHandler<string>? TitleChanged;

    /// <summary>Feeds raw output bytes from the remote/child process into the emulator, updating grid, cursor, and title state.</summary>
    void Feed(ReadOnlySpan<byte> data);

    /// <summary>Resizes the active and alternate screens, clamping the cursor to remain in bounds.</summary>
    void Resize(int cols, int rows);

    /// <summary>Returns the current screen as a [row, col] cell array.</summary>
    Cell[,] GetScreenSnapshot();

    /// <summary>
    /// Returns a scrollback line by logical index (0 = oldest, Count-1 = most recent).
    /// </summary>
    Cell[] GetScrollbackLine(int index);
}
