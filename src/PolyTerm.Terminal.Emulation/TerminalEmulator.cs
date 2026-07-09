using System.Text;
using Microsoft.Extensions.Logging;
using PolyTerm.Terminal.Parser;

namespace PolyTerm.Terminal;

/// <summary>
/// Headless VT/xterm terminal emulator. Consumes raw bytes, maintains cell grid + scrollback.
/// Raises <see cref="ScreenUpdated"/> when the display changes (batching handled by the surface layer).
/// Conformance targets: vim, htop, tmux, mc, nano.
/// </summary>
public sealed class TerminalEmulator : IVtHandler, ITerminalEmulator
{
    private readonly ILogger<TerminalEmulator> _logger;
    private readonly VtParser _parser;

    // Grid and scrollback
    private CellGrid _active;
    private CellGrid _alternate;
    private bool _usingAlternate;
    private CellGrid Screen => _usingAlternate ? _alternate : _active;

    /// <summary>The scrollback buffer backing this emulator's history. Lines are pushed here as the active screen scrolls.</summary>
    public ScrollbackBuffer Scrollback { get; }

    // Cursor
    private int _cursorCol;
    private int _cursorRow;
    private bool _cursorVisible = true;

    // Saved cursor (DECSC)
    private int _savedCol;
    private int _savedRow;
    private CellAttribute _savedAttr;

    // Current SGR attributes
    private CellAttribute _currentAttr = CellAttribute.Default;

    // Scroll region (0-based, inclusive)
    private int _scrollTop;
    private int _scrollBottom;

    // Modes
    private bool _autowrap = true;
    private bool _originMode; // DECOM — cursor is constrained to scroll region top when true
    private bool _bracketedPaste;

    // Title
    private string _title = string.Empty;

    /// <inheritdoc/>
    public int Cols => Screen.Cols;
    /// <inheritdoc/>
    public int Rows => Screen.Rows;
    /// <inheritdoc/>
    public int CursorCol => _cursorCol;
    /// <inheritdoc/>
    public int CursorRow => _cursorRow;
    /// <inheritdoc/>
    public bool CursorVisible => _cursorVisible;
    /// <inheritdoc/>
    public string Title => _title;

    /// <inheritdoc/>
    public event EventHandler? ScreenUpdated;
    /// <inheritdoc/>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Creates an emulator with the given screen size and scrollback capacity.</summary>
    public TerminalEmulator(int cols, int rows, int scrollbackLines, ILogger<TerminalEmulator> logger)
    {
        _logger = logger;
        _parser = new VtParser(this);
        _active    = new CellGrid(cols, rows);
        _alternate = new CellGrid(cols, rows);
        Scrollback = new ScrollbackBuffer(scrollbackLines);
        _scrollBottom = rows - 1;
    }

    /// <inheritdoc/>
    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Feed(data);
        ScreenUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Resize(int cols, int rows)
    {
        _active.Resize(cols, rows);
        _alternate.Resize(cols, rows);
        _cursorCol    = Math.Min(_cursorCol, cols - 1);
        _cursorRow    = Math.Min(_cursorRow, rows - 1);
        _scrollTop    = 0;
        _scrollBottom = rows - 1;
        ScreenUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public Cell[,] GetScreenSnapshot()
    {
        var snap = Screen.TakeSnapshot();
        var result = new Cell[Rows, Cols];
        for (var r = 0; r < Rows; r++)
        for (var c = 0; c < Cols; c++)
            result[r, c] = snap[r * Cols + c];
        return result;
    }

    /// <inheritdoc/>
    public int ScrollbackLineCount => Scrollback.Count;
    /// <inheritdoc/>
    public Cell[] GetScrollbackLine(int index) => Scrollback[index];

    // ─── IVtHandler ───────────────────────────────────────────────────────────

    void IVtHandler.Print(char ch) => PrintCodepoint(ch);
    void IVtHandler.PrintRune(int codepoint) => PrintCodepoint(codepoint);

    private void PrintCodepoint(int codepoint)
    {
        // Determine column width (simplified: CJK double-width > 0x2E80)
        var isWide = codepoint >= 0x1100 && IsWideCharacter(codepoint);
        var width = isWide ? 2 : 1;

        if (_autowrap && _cursorCol + width > Cols)
        {
            CarriageReturn();
            LineFeed();
        }

        ref var cell = ref Screen[_cursorCol, _cursorRow];
        cell.Codepoint = codepoint;
        cell.Attribute = _currentAttr;
        cell.IsWide    = isWide;
        cell.IsContinuation = false;

        if (isWide && _cursorCol + 1 < Cols)
        {
            ref var right = ref Screen[_cursorCol + 1, _cursorRow];
            right = Cell.Empty;
            right.IsContinuation = true;
        }

        _cursorCol += width;
        if (_cursorCol >= Cols) _cursorCol = Cols - 1;
    }

    void IVtHandler.Execute(byte b)
    {
        switch (b)
        {
            case 0x07: // BEL — ignore for now (could raise event)
                break;
            case 0x08: // BS
                if (_cursorCol > 0) _cursorCol--;
                break;
            case 0x09: // HT (tab)
                _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, Cols - 1);
                break;
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                LineFeed();
                break;
            case 0x0D: // CR
                CarriageReturn();
                break;
            case 0x0E: // SO — switch to G1 charset (not fully impl)
            case 0x0F: // SI — switch to G0 charset (not fully impl)
                break;
            case 0x7F: // DEL — ignore
                break;
        }
    }

    void IVtHandler.EscDispatch(ReadOnlySpan<byte> intermediates, byte finalByte)
    {
        switch (finalByte)
        {
            case (byte)'7': // DECSC — save cursor
                _savedCol  = _cursorCol;
                _savedRow  = _cursorRow;
                _savedAttr = _currentAttr;
                break;
            case (byte)'8': // DECRC — restore cursor
                _cursorCol  = _savedCol;
                _cursorRow  = _savedRow;
                _currentAttr = _savedAttr;
                break;
            case (byte)'D': // IND — linefeed
                LineFeed();
                break;
            case (byte)'E': // NEL — next line
                CarriageReturn();
                LineFeed();
                break;
            case (byte)'M': // RI — reverse index
                if (_cursorRow == _scrollTop)
                    Screen.ScrollDown(1, _scrollTop, _scrollBottom);
                else if (_cursorRow > 0)
                    _cursorRow--;
                break;
            case (byte)'c': // RIS — full reset
                FullReset();
                break;
            case (byte)'=' or (byte)'>': // DECKPAM/DECKPNM — ignored
                break;
        }
    }

    void IVtHandler.CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> intermediates, byte finalByte)
    {
        var p0 = parameters.Length > 0 ? parameters[0] : 0;
        var p1 = parameters.Length > 1 ? parameters[1] : 0;

        switch (finalByte)
        {
            case (byte)'A': MoveCursor(0, -(Math.Max(p0, 1))); break; // CUU
            case (byte)'B': MoveCursor(0, Math.Max(p0, 1)); break;    // CUD
            case (byte)'C': MoveCursor(Math.Max(p0, 1), 0); break;    // CUF
            case (byte)'D': MoveCursor(-(Math.Max(p0, 1)), 0); break; // CUB
            case (byte)'E': _cursorCol = 0; MoveCursor(0, Math.Max(p0, 1)); break; // CNL
            case (byte)'F': _cursorCol = 0; MoveCursor(0, -(Math.Max(p0, 1))); break; // CPL
            case (byte)'G': SetCursor(Math.Max(p0, 1) - 1, _cursorRow); break; // CHA
            case (byte)'H': case (byte)'f': // CUP / HVP
                SetCursor(Math.Max(p1, 1) - 1, Math.Max(p0, 1) - 1);
                break;
            case (byte)'J': EraseInDisplay(p0); break;
            case (byte)'K': EraseInLine(p0); break;
            case (byte)'L': InsertLines(Math.Max(p0, 1)); break;
            case (byte)'M': DeleteLines(Math.Max(p0, 1)); break;
            case (byte)'P': DeleteChars(Math.Max(p0, 1)); break;
            case (byte)'S': Screen.ScrollUp(Math.Max(p0, 1), _scrollTop, _scrollBottom); break;
            case (byte)'T': Screen.ScrollDown(Math.Max(p0, 1), _scrollTop, _scrollBottom); break;
            case (byte)'X': // ECH
                Screen.ClearRegion(_cursorCol, _cursorRow, _cursorCol + Math.Max(p0, 1) - 1, _currentAttr);
                break;
            case (byte)'d': SetCursor(_cursorCol, Math.Max(p0, 1) - 1); break; // VPA
            case (byte)'m': ApplySgr(parameters); break;
            case (byte)'r': // DECSTBM — set scrolling region
                _scrollTop    = Math.Max(p0, 1) - 1;
                _scrollBottom = (p1 == 0 ? Rows : Math.Min(p1, Rows)) - 1;
                SetCursor(0, 0);
                break;
            case (byte)'h': ApplyMode(parameters, intermediates, true); break;
            case (byte)'l': ApplyMode(parameters, intermediates, false); break;
            case (byte)'s': _savedCol = _cursorCol; _savedRow = _cursorRow; break; // SCP
            case (byte)'u': _cursorCol = _savedCol; _cursorRow = _savedRow; break; // RCP
            default:
                _logger.LogTrace("Unhandled CSI {Final} params=[{Params}]", (char)finalByte, string.Join(",", parameters.ToArray()));
                break;
        }
    }

    void IVtHandler.OscDispatch(int code, string data)
    {
        switch (code)
        {
            case 0: case 1: case 2: // set icon/title
                _title = data;
                TitleChanged?.Invoke(this, data);
                break;
            case 52: // clipboard — gated; ignore by default
                break;
            default:
                _logger.LogTrace("Unhandled OSC {Code} {Data}", code, data);
                break;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
        {
            // Push top row into scrollback before scrolling
            if (!_usingAlternate)
            {
                var row = new Cell[Cols];
                for (var c = 0; c < Cols; c++)
                    row[c] = Screen[c, _scrollTop];
                Scrollback.Push(row);
            }
            Screen.ScrollUp(1, _scrollTop, _scrollBottom);
        }
        else if (_cursorRow < Rows - 1)
        {
            _cursorRow++;
        }
    }

    private void CarriageReturn() => _cursorCol = 0;

    private void MoveCursor(int dcol, int drow)
    {
        _cursorCol = Math.Clamp(_cursorCol + dcol, 0, Cols - 1);
        _cursorRow = Math.Clamp(_cursorRow + drow, 0, Rows - 1);
    }

    private void SetCursor(int col, int row)
    {
        var rowMin = _originMode ? _scrollTop : 0;
        var rowMax = _originMode ? _scrollBottom : Rows - 1;
        _cursorCol = Math.Clamp(col, 0, Cols - 1);
        _cursorRow = Math.Clamp(row, rowMin, rowMax);
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // from cursor to end
                Screen.ClearRegion(_cursorCol, _cursorRow, Cols - 1, _currentAttr);
                for (var r = _cursorRow + 1; r < Rows; r++) Screen.ClearRow(r, _currentAttr);
                break;
            case 1: // from beginning to cursor
                for (var r = 0; r < _cursorRow; r++) Screen.ClearRow(r, _currentAttr);
                Screen.ClearRegion(0, _cursorRow, _cursorCol, _currentAttr);
                break;
            case 2: case 3: // entire screen
                for (var r = 0; r < Rows; r++) Screen.ClearRow(r, _currentAttr);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: Screen.ClearRegion(_cursorCol, _cursorRow, Cols - 1, _currentAttr); break;
            case 1: Screen.ClearRegion(0, _cursorRow, _cursorCol, _currentAttr); break;
            case 2: Screen.ClearRow(_cursorRow, _currentAttr); break;
        }
    }

    private void InsertLines(int n) => Screen.ScrollDown(n, _cursorRow, _scrollBottom);
    private void DeleteLines(int n) => Screen.ScrollUp(n, _cursorRow, _scrollBottom);

    private void DeleteChars(int n)
    {
        var end = Cols - 1;
        for (var c = _cursorCol; c <= end - n; c++)
            Screen[c, _cursorRow] = Screen[c + n, _cursorRow];
        Screen.ClearRegion(end - n + 1, _cursorRow, end, _currentAttr);
    }

    private void ApplySgr(ReadOnlySpan<int> parameters)
    {
        if (parameters.IsEmpty) { _currentAttr = CellAttribute.Default; return; }

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            switch (p)
            {
                case 0:  _currentAttr = CellAttribute.Default; break;
                case 1:  _currentAttr.Bold = true; break;
                case 2:  _currentAttr.Dim = true; break;
                case 3:  _currentAttr.Italic = true; break;
                case 4:  _currentAttr.Underline = true; break;
                case 5:  _currentAttr.Blink = true; break;
                case 6:  _currentAttr.RapidBlink = true; break;
                case 7:  _currentAttr.Reverse = true; break;
                case 8:  _currentAttr.Invisible = true; break;
                case 9:  _currentAttr.Strikethrough = true; break;
                case 21: _currentAttr.DoubleUnderline = true; break;
                case 22: _currentAttr.Bold = false; _currentAttr.Dim = false; break;
                case 23: _currentAttr.Italic = false; break;
                case 24: _currentAttr.Underline = false; break;
                case 25: _currentAttr.Blink = false; _currentAttr.RapidBlink = false; break;
                case 27: _currentAttr.Reverse = false; break;
                case 28: _currentAttr.Invisible = false; break;
                case 29: _currentAttr.Strikethrough = false; break;
                // Standard foreground colors 30-37
                case >= 30 and <= 37: _currentAttr.Foreground = TerminalColor.Named((byte)(p - 30)); break;
                case 38: // extended foreground
                    i = ParseExtendedColor(parameters, i, ref _currentAttr.Foreground);
                    break;
                case 39: _currentAttr.Foreground = TerminalColor.Default; break;
                // Standard background colors 40-47
                case >= 40 and <= 47: _currentAttr.Background = TerminalColor.Named((byte)(p - 40)); break;
                case 48: // extended background
                    i = ParseExtendedColor(parameters, i, ref _currentAttr.Background);
                    break;
                case 49: _currentAttr.Background = TerminalColor.Default; break;
                // Bright foreground 90-97
                case >= 90 and <= 97: _currentAttr.Foreground = TerminalColor.Named((byte)(p - 90 + 8)); break;
                // Bright background 100-107
                case >= 100 and <= 107: _currentAttr.Background = TerminalColor.Named((byte)(p - 100 + 8)); break;
            }
        }
    }

    private static int ParseExtendedColor(ReadOnlySpan<int> parameters, int i, ref TerminalColor color)
    {
        i++;
        if (i >= parameters.Length) return i;
        switch (parameters[i])
        {
            case 5: // 256 color
                i++;
                if (i < parameters.Length)
                    color = TerminalColor.Indexed((byte)parameters[i]);
                break;
            case 2: // truecolor
                if (i + 3 < parameters.Length)
                {
                    color = TerminalColor.Rgb((byte)parameters[i+1], (byte)parameters[i+2], (byte)parameters[i+3]);
                    i += 3;
                }
                break;
        }
        return i;
    }

    private void ApplyMode(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> intermediates, bool enable)
    {
        var isPrivate = intermediates.Length > 0 && intermediates[0] == (byte)'?';
        foreach (var p in parameters)
        {
            if (isPrivate)
            {
                switch (p)
                {
                    case 1:  /* DECCKM cursor keys — TODO */ break;
                    case 6:  _originMode = enable; break;
                    case 7:  _autowrap = enable; break;
                    case 12: /* cursor blink — ignore */ break;
                    case 25: _cursorVisible = enable; break;
                    case 47: case 1047: SwitchScreen(enable); break;
                    case 1049: // save/restore + switch screen
                        if (enable) { _savedCol = _cursorCol; _savedRow = _cursorRow; }
                        SwitchScreen(enable);
                        if (!enable) { _cursorCol = _savedCol; _cursorRow = _savedRow; }
                        break;
                    case 2004: _bracketedPaste = enable; break;
                }
            }
            else
            {
                switch (p)
                {
                    case 4: /* IRM insert mode — TODO */ break;
                    case 20: /* LNM — TODO */ break;
                }
            }
        }
    }

    private void SwitchScreen(bool toAlternate)
    {
        if (toAlternate == _usingAlternate) return;
        _usingAlternate = toAlternate;
        if (toAlternate)
            _alternate = new CellGrid(Cols, Rows); // clear alternate on entry
    }

    private void FullReset()
    {
        _active    = new CellGrid(Cols, Rows);
        _alternate = new CellGrid(Cols, Rows);
        _usingAlternate = false;
        _cursorCol = 0; _cursorRow = 0;
        _savedCol  = 0; _savedRow  = 0;
        _scrollTop = 0; _scrollBottom = Rows - 1;
        _currentAttr = CellAttribute.Default;
        _savedAttr   = CellAttribute.Default;
        _autowrap    = true;
        _originMode  = false;
        _cursorVisible = true;
        _title = string.Empty;
    }

    private static bool IsWideCharacter(int codepoint) =>
        (codepoint >= 0x1100 && codepoint <= 0x115F)  ||
        (codepoint >= 0x2E80 && codepoint <= 0x303F)  ||
        (codepoint >= 0x3040 && codepoint <= 0xA4CF)  ||
        (codepoint >= 0xAC00 && codepoint <= 0xD7A3)  ||
        (codepoint >= 0xF900 && codepoint <= 0xFAFF)  ||
        (codepoint >= 0xFE30 && codepoint <= 0xFE4F)  ||
        (codepoint >= 0xFF01 && codepoint <= 0xFF60)  ||
        (codepoint >= 0xFFE0 && codepoint <= 0xFFE6)  ||
        (codepoint >= 0x1F300 && codepoint <= 0x1F9FF);
}
