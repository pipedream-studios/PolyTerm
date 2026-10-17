using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using PolyTerm.Engine;
using PolyTerm.Terminal;

namespace PolyTerm.App.Surfaces.Terminal;

/// <summary>
/// Avalonia custom control that renders an <see cref="ITerminalEmulator"/> cell grid.
/// Attached to an <see cref="ITerminalChannel"/>: engine output → emulator → render.
/// Input from keyboard/mouse → channel.
/// Supports mouse-wheel scrollback and text selection with Ctrl+Shift+C copy.
/// Future milestones: emulator type is selectable via <see cref="ITerminalEmulator"/>.
/// </summary>
public sealed class TerminalSurface : Control
{
    // ─── Styled Properties ────────────────────────────────────────────────────

    /// <summary>
    /// Backs <see cref="Channel"/>. Changing this reattaches the surface to the new channel
    /// (or detaches if set to <c>null</c>), tearing down the previous emulator/subscription first.
    /// </summary>
    public static readonly StyledProperty<ITerminalChannel?> ChannelProperty =
        AvaloniaProperty.Register<TerminalSurface, ITerminalChannel?>(nameof(Channel));

    /// <summary>Backs <see cref="TerminalFont"/>.</summary>
    public static readonly StyledProperty<FontFamily> TerminalFontProperty =
        AvaloniaProperty.Register<TerminalSurface, FontFamily>(nameof(TerminalFont),
            new FontFamily("Cascadia Code,Consolas,Courier New,monospace"));

    /// <summary>Backs <see cref="FontSize"/>.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalSurface, double>(nameof(FontSize), 14.0);

    /// <summary>
    /// Updated whenever the remote process sends an OSC 0/1/2 title sequence.
    /// Bind this to a view-model property with <c>Mode=OneWayToSource</c> to propagate
    /// the emulator title without coupling the surface to a specific view model.
    /// </summary>
    public static readonly StyledProperty<string> EmulatorTitleProperty =
        AvaloniaProperty.Register<TerminalSurface, string>(nameof(EmulatorTitle), "");

    /// <summary>
    /// The terminal channel this surface renders and sends input to. Setting a new value (or
    /// <c>null</c>) safely detaches from any previous channel and attaches to the new one.
    /// </summary>
    public ITerminalChannel? Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    /// <summary>The monospace font used to render cells.</summary>
    public FontFamily TerminalFont
    {
        get => GetValue(TerminalFontProperty);
        set => SetValue(TerminalFontProperty, value);
    }

    /// <summary>Font size in points used to render cells.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The terminal title most recently reported by the remote process via OSC 0/1/2.</summary>
    public string EmulatorTitle
    {
        get => GetValue(EmulatorTitleProperty);
        set => SetValue(EmulatorTitleProperty, value);
    }

    // ─── Internal state ───────────────────────────────────────────────────────

    private ITerminalEmulator? _emulator;
    private ITerminalChannel? _attachedChannel;

    private double _cellWidth;
    private double _cellHeight;

    // Coalescing: batch dirty invalidations on a 16 ms timer
    private volatile bool _pendingRender;
    private readonly DispatcherTimer _renderTimer;

    // Scrollback: 0 = live bottom, N = N lines scrolled back
    private int _scrollOffset;

    // The emulator is fed on the channel's read thread but rendered, resized, scrolled and copied from on
    // the UI thread; it has no locking of its own, so every access (and _scrollOffset) goes through this.
    // While attached to a channel this is that channel's state lock, since the emulator is shared with it.
    private object _emuLock = new();
    private TerminalChannelState? _state;

    // Text selection: view-space (col, row) coordinates, null = no selection
    private (int Col, int Row)? _selStart;
    private (int Col, int Row)? _selEnd;
    private bool _selecting;

    public TerminalSurface()
    {
        Focusable = true;
        ClipToBounds = true;

        ContextMenu = BuildContextMenu();

        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (_pendingRender) { _pendingRender = false; InvalidateVisual(); } });
    }

    // ─── Visual tree lifecycle ────────────────────────────────────────────────

    /// <summary>Starts the coalesced render timer and requests focus once the control is loaded.</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
        if (_state is null && Channel is { } channel) Reattach(channel);
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Stops the render timer and unsubscribes from the attached channel, if any, to avoid leaking the event handler.</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderTimer.Stop();
        // The emulator stays with the channel; only this surface's subscriptions are dropped.
        DetachState();
    }

    /// <summary>Reattaches to a new <see cref="Channel"/> when it changes, and re-measures cell size when the font or font size changes.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChannelProperty)
            Reattach(change.NewValue as ITerminalChannel);

        if (change.Property == FontSizeProperty || change.Property == TerminalFontProperty)
        {
            MeasureCell();
            ApplyGridSize(Bounds.Size);
            _pendingRender = true;
        }
    }

    // ─── Channel attachment ───────────────────────────────────────────────────

    /// <summary>
    /// Tears down the current emulator and channel subscription (if any) and, when
    /// <paramref name="channel"/> is non-null, creates a fresh emulator sized to the control
    /// and subscribes to its output. Safe to call with the same channel, a different channel,
    /// or <c>null</c> at any time — this is what lets SSH auto-reconnect swap in a new channel
    /// on a live surface without leaving stale subscriptions or emulator state behind.
    /// </summary>
    private void Reattach(ITerminalChannel? channel)
    {
        DetachState();

        _attachedChannel = channel;
        _selStart = null;
        _selEnd = null;

        if (channel is not null)
        {
            // The emulator belongs to the channel, so a surface re-created after a tab switch picks up the
            // existing screen and scrollback instead of starting blank.
            var (cols, rows) = GridSize();
            var state = TerminalChannelState.For(channel, cols, rows);
            _state = state;
            state.ScreenUpdated += OnStateScreenUpdated;
            state.TitleChanged  += OnEmulatorTitleChanged;
            state.Fed           += OnStateFed;
            _emuLock = state.Lock;
            lock (_emuLock) { _scrollOffset = 0; _emulator = state.Emulator; }
            _pendingRender = true;
            // Size may differ from when the emulator was created (e.g. different tab layout).
            ApplyGridSize(Bounds.Size);
        }
        else
        {
            lock (_emuLock) { _scrollOffset = 0; _emulator = null; }
            Dispatcher.UIThread.Post(() => EmulatorTitle = "");
        }

        InvalidateVisual();
    }

    private void DetachState()
    {
        if (_state is { } state)
        {
            state.ScreenUpdated -= OnStateScreenUpdated;
            state.TitleChanged  -= OnEmulatorTitleChanged;
            state.Fed           -= OnStateFed;
            _state = null;
        }
    }

    private void OnStateScreenUpdated() => _pendingRender = true;

    // Runs under the state's lock (the same object as _emuLock while attached).
    private void OnStateFed(int linesPushed)
    {
        // At the live bottom the view simply follows the output. When scrolled back, keep the same
        // lines on screen by moving the offset along with the lines pushed into scrollback.
        if (_scrollOffset > 0 && _emulator is not null)
            _scrollOffset = Math.Min(_scrollOffset + linesPushed, _emulator.ScrollbackLineCount);
    }

    private void OnEmulatorTitleChanged(string title)
    {
        // TitleChanged fires on the thread that called Feed (typically a background read loop).
        // EmulatorTitle is a StyledProperty, so it must be set on the UI thread.
        Dispatcher.UIThread.Post(() => EmulatorTitle = title);
    }

    private void SnapToBottom() { lock (_emuLock) _scrollOffset = 0; }

    // ─── Layout helpers ───────────────────────────────────────────────────────

    private (int cols, int rows) GridSize()
    {
        MeasureCell();
        var w = Math.Max(Bounds.Width, 80 * _cellWidth);
        var h = Math.Max(Bounds.Height, 24 * _cellHeight);
        return ((int)(w / _cellWidth), (int)(h / _cellHeight));
    }

    private void MeasureCell()
    {
        var tf = new Typeface(TerminalFont);
        var ft = new FormattedText("M", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, FontSize, Brushes.White);
        _cellWidth  = ft.Width;
        _cellHeight = ft.Height;
    }

    // ─── Input handling ───────────────────────────────────────────────────────

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_attachedChannel is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        SnapToBottom();
        _ = _attachedChannel.SendInputAsync(Encoding.UTF8.GetBytes(e.Text)).AsTask();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_attachedChannel is null || e.Handled) return;

        // Ctrl+Shift+C → copy selection to clipboard
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
        {
            var text = GetSelectedText();
            if (!string.IsNullOrEmpty(text))
            {
                e.Handled = true;
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
            }
            return;
        }

        // Ctrl+Shift+V → paste from clipboard
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.V)
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        // Shift+Insert → paste from clipboard
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Insert)
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        // Ctrl+letter → control characters ^A (0x01) … ^Z (0x1A)
        // Require ONLY Ctrl (not Ctrl+Shift) to avoid stealing Ctrl+Shift+C/V above
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.A and <= Key.Z)
        {
            e.Handled = true;
            SnapToBottom();
            var ctrl = (byte)((int)e.Key - (int)Key.A + 1);
            _ = _attachedChannel.SendInputAsync(new[] { ctrl }).AsTask();
            return;
        }

        var bytes = KeyToBytes(e, _emulator?.CursorKeyApplicationMode ?? false);
        if (bytes is null) return;
        e.Handled = true;
        SnapToBottom();
        _ = _attachedChannel.SendInputAsync(bytes).AsTask();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyGridSize(e.NewSize);
    }

    /// <summary>Resizes the emulator and the remote PTY to however many cells fit in <paramref name="size"/>.</summary>
    private void ApplyGridSize(Size size)
    {
        if (_emulator is null || _cellWidth == 0 || _cellHeight == 0 || size.Width <= 0 || size.Height <= 0) return;
        var cols = Math.Max(1, (int)(size.Width / _cellWidth));
        var rows = Math.Max(1, (int)(size.Height / _cellHeight));
        lock (_emuLock)
        {
            _emulator?.Resize(cols, rows);
            _scrollOffset = Math.Min(_scrollOffset, _emulator?.ScrollbackLineCount ?? 0);
        }
        _ = _attachedChannel?.ResizeAsync(cols, rows).AsTask();
    }

    // ─── Mouse input ──────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_emulator is null) return;

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            ChangeFontSize(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        var lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * 3));
        lock (_emuLock)
        {
            if (_emulator is null) return;
            if (e.Delta.Y > 0)
                _scrollOffset = Math.Min(_scrollOffset + lines, _emulator.ScrollbackLineCount);
            else
                _scrollOffset = Math.Max(_scrollOffset - lines, 0);
        }

        _pendingRender = true;
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selStart  = HitTest(e.GetPosition(this));
            _selEnd    = _selStart;
            _selecting = true;
            _pendingRender = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _selecting = false; return; }
        _selEnd = HitTest(e.GetPosition(this));
        _pendingRender = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        // Single left click (no drag) = clear selection. A right click keeps it so the menu can Copy it.
        if (e.InitialPressMouseButton == MouseButton.Left && _selStart == _selEnd) { _selStart = null; _selEnd = null; _pendingRender = true; }
    }

    private (int Col, int Row) HitTest(Point p)
    {
        int cols, rows;
        lock (_emuLock) { cols = _emulator?.Cols ?? 1; rows = _emulator?.Rows ?? 1; }
        return ComputeHitTest(p.X, p.Y, _cellWidth, _cellHeight, cols, rows);
    }

    /// <summary>Pure pixel-to-cell mapping core of <see cref="HitTest"/>, extracted so it's unit-testable without a live control/attached emulator (which <see cref="HitTest"/> needs only for its real cell size and grid dimensions).</summary>
    internal static (int Col, int Row) ComputeHitTest(double pointerX, double pointerY, double cellWidth, double cellHeight, int cols, int rows) =>
        (Math.Clamp((int)(pointerX / cellWidth), 0, cols - 1),
         Math.Clamp((int)(pointerY / cellHeight), 0, rows - 1));

    // ─── Context menu ─────────────────────────────────────────────────────────

    private const double MinFontSize = 6, MaxFontSize = 48;

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string header, Action action, KeyGesture? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture };
            item.Click += (_, _) => { action(); Focus(); };
            return item;
        }

        var copy         = Item("Copy", CopySelection);
        var copyAll      = Item("Copy All", CopyAll);
        var paste        = Item("Paste", () => _ = PasteFromClipboardAsync(), new KeyGesture(Key.Insert, KeyModifiers.Shift));
        var selectAll    = Item("Select All", SelectAll);
        var bigger       = Item("Increase font size", () => ChangeFontSize(1));
        var smaller      = Item("Decrease font size", () => ChangeFontSize(-1));
        var scrollBottom = Item("Scroll to bottom", SnapToBottomAndRender);
        var clear        = Item("Clear Scrollback", ClearScrollback);
        var reset        = Item("Reset Terminal", ResetTerminal);

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(bigger);
        menu.Items.Add(smaller);
        menu.Items.Add(new Separator());
        menu.Items.Add(scrollBottom);
        menu.Items.Add(clear);
        menu.Items.Add(reset);

        menu.Opening += (_, _) =>
        {
            copy.IsEnabled         = !string.IsNullOrEmpty(GetSelectedText());
            copyAll.IsEnabled      = _emulator is not null;
            paste.IsEnabled        = _attachedChannel is not null;
            selectAll.IsEnabled    = _emulator is not null;
            clear.IsEnabled        = _emulator is not null;
            reset.IsEnabled        = _emulator is not null;
            lock (_emuLock) scrollBottom.IsVisible = _scrollOffset > 0;
        };
        return menu;
    }

    private void ChangeFontSize(int step) =>
        FontSize = Math.Clamp(FontSize + step, MinFontSize, MaxFontSize);

    private void SnapToBottomAndRender()
    {
        SnapToBottom();
        _pendingRender = true;
    }

    private void CopySelection()
    {
        var text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private void CopyAll()
    {
        var text = GetAllText();
        if (!string.IsNullOrEmpty(text))
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>Selects the whole visible screen (selection is view-space, so it covers what is currently on screen).</summary>
    private void SelectAll()
    {
        int cols, rows;
        lock (_emuLock)
        {
            if (_emulator is null) return;
            cols = _emulator.Cols;
            rows = _emulator.Rows;
        }
        _selStart = (0, 0);
        _selEnd   = (cols - 1, rows - 1);
        _pendingRender = true;
    }

    private void ClearScrollback()
    {
        lock (_emuLock)
        {
            _emulator?.ClearScrollback();
            _scrollOffset = 0;
        }
        _selStart = null;
        _selEnd   = null;
        _pendingRender = true;
    }

    /// <summary>Resets the local emulator state (RIS) - colours, modes, screen. Does not touch the remote side.</summary>
    private void ResetTerminal()
    {
        lock (_emuLock)
        {
            _emulator?.Feed("\u001bc"u8);
            _scrollOffset = 0;
        }
        _selStart = null;
        _selEnd   = null;
        _pendingRender = true;
    }

    /// <summary>Every scrollback line followed by the current screen as plain text, with trailing blanks trimmed.</summary>
    private string GetAllText()
    {
        lock (_emuLock)
        {
            if (_emulator is null) return "";
            var lines = new List<string>();

            static string RowText(Cell[] row)
            {
                var sb = new StringBuilder(row.Length);
                foreach (var cell in row)
                {
                    if (cell.IsContinuation) continue;
                    sb.Append(cell.IsBlank ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
                }
                return sb.ToString().TrimEnd();
            }

            for (var i = 0; i < _emulator.ScrollbackLineCount; i++)
                lines.Add(RowText(_emulator.GetScrollbackLine(i)));

            var snap = _emulator.GetScreenSnapshot();
            for (var r = 0; r < _emulator.Rows; r++)
            {
                var row = new Cell[_emulator.Cols];
                for (var c = 0; c < row.Length; c++) row[c] = snap[r, c];
                lines.Add(RowText(row));
            }

            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ─── Clipboard ────────────────────────────────────────────────────────────

    private async Task PasteFromClipboardAsync()
    {
        var text = await (TopLevel.GetTopLevel(this)?.Clipboard?.TryGetTextAsync()
            ?? Task.FromResult<string?>(null)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text) || _attachedChannel is null) return;
        SnapToBottom();
        await _attachedChannel.SendInputAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
    }

    // ─── Render ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The rows × cols cells to draw for a view scrolled back by <paramref name="scrollOffset"/> lines: scrollback
    /// lines first, then the live screen. The live screen starts at display row <c>scrollOffset</c> (0 at the
    /// bottom), so it is visible whenever the view is scrolled back by less than a screenful.
    /// </summary>
    internal static Cell[][] BuildView(ITerminalEmulator emulator, int scrollOffset)
    {
        var viewRows = emulator.Rows;
        var viewCols = emulator.Cols;
        var scrollbackCount = emulator.ScrollbackLineCount;
        var screenSnap = scrollOffset < viewRows ? emulator.GetScreenSnapshot() : null;

        var view = new Cell[viewRows][];
        for (var displayRow = 0; displayRow < viewRows; displayRow++)
        {
            var row = new Cell[viewCols];
            var virtualLine = scrollbackCount - scrollOffset + displayRow;
            if (virtualLine >= 0 && virtualLine < scrollbackCount)
            {
                var sbRow = emulator.GetScrollbackLine(virtualLine);
                Array.Copy(sbRow, row, Math.Min(sbRow.Length, viewCols));
            }
            else if (virtualLine >= scrollbackCount && screenSnap is not null)
            {
                var screenRow = virtualLine - scrollbackCount;
                if (screenRow < viewRows)
                    for (var col = 0; col < viewCols; col++)
                        row[col] = screenSnap[screenRow, col];
            }
            view[displayRow] = row;
        }
        return view;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), new Rect(Bounds.Size));

        MeasureCell();
        var typeface = new Typeface(TerminalFont);

        // Copy the visible cells (and cursor state) under the lock, then draw from the copy so the
        // output thread is never blocked on text layout and never mutates what we're drawing.
        Cell[][] view;
        int viewRows, viewCols, scrollOffset, cursorCol, cursorRow;
        bool cursorVisible;
        lock (_emuLock)
        {
            if (_emulator is null) return;
            viewRows = _emulator.Rows;
            viewCols = _emulator.Cols;
            var scrollbackCount = _emulator.ScrollbackLineCount;
            _scrollOffset = Math.Min(_scrollOffset, scrollbackCount);
            scrollOffset  = _scrollOffset;
            cursorCol     = _emulator.CursorCol;
            cursorRow     = _emulator.CursorRow;
            cursorVisible = _emulator.CursorVisible;

            view = BuildView(_emulator, scrollOffset);
        }

        var isScrolled = scrollOffset > 0;

        for (var displayRow = 0; displayRow < viewRows; displayRow++)
        {
            for (var col = 0; col < viewCols; col++)
            {
                var cell = view[displayRow][col];

                if (cell.IsContinuation) continue;

                var selected = IsInSelection(displayRow, col);
                if (cell.IsBlank && cell.Attribute == CellAttribute.Default && !selected) continue;

                var x = col * _cellWidth;
                var y = displayRow * _cellHeight;
                var w = cell.IsWide ? _cellWidth * 2 : _cellWidth;

                // Background
                if (selected)
                    context.FillRectangle(s_selectionBrush, new Rect(x, y, w, _cellHeight));
                else if (cell.Attribute.Background != TerminalColor.Default)
                    context.FillRectangle(BrushForColor(cell.Attribute.Background, false), new Rect(x, y, w, _cellHeight));

                // Glyph
                if (!cell.IsBlank)
                {
                    var fg = selected ? Brushes.White : BrushForColor(cell.Attribute.Foreground, true);
                    var ft = new FormattedText(char.ConvertFromUtf32(cell.Codepoint),
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, FontSize, fg);
                    context.DrawText(ft, new Point(x, y));
                }
            }
        }

        // Cursor — only shown when at the live bottom
        if (!isScrolled && cursorVisible)
        {
            var cx = cursorCol * _cellWidth;
            var cy = cursorRow * _cellHeight;
            context.DrawRectangle(Brushes.White, null, new Rect(cx, cy + _cellHeight - 2, _cellWidth, 2));
        }

        // Scrollback indicator
        if (isScrolled)
        {
            const string hint = "Scroll: ↑↓ wheel  •  Copy: Ctrl+Shift+C";
            var ft = new FormattedText($"↑ {scrollOffset} lines  {hint}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11,
                new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)));
            var bw = ft.Width + 12;
            var bh = ft.Height + 6;
            var bx = Bounds.Width - bw - 4;
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(200, 0x12, 0x12, 0x12)),
                new Rect(bx, 4, bw, bh));
            context.DrawText(ft, new Point(bx + 6, 7));
        }
    }

    // ─── Selection helpers ────────────────────────────────────────────────────

    private bool IsInSelection(int displayRow, int col)
    {
        if (_selStart is null || _selEnd is null) return false;

        var (c1, r1) = _selStart.Value;
        var (c2, r2) = _selEnd.Value;

        // Normalize so r1/c1 ≤ r2/c2
        if (r1 > r2 || (r1 == r2 && c1 > c2)) { (c1, r1, c2, r2) = (c2, r2, c1, r1); }

        if (displayRow < r1 || displayRow > r2) return false;
        if (displayRow == r1 && col < c1) return false;
        if (displayRow == r2 && col > c2) return false;
        return true;
    }

    private string GetSelectedText()
    {
        lock (_emuLock) return GetSelectedTextLocked();
    }

    private string GetSelectedTextLocked()
    {
        if (_selStart is null || _selEnd is null || _emulator is null) return "";

        var (c1, r1) = _selStart.Value;
        var (c2, r2) = _selEnd.Value;
        if (r1 > r2 || (r1 == r2 && c1 > c2)) { (c1, r1, c2, r2) = (c2, r2, c1, r1); }

        var scrollbackCount = _emulator.ScrollbackLineCount;
        var sb = new StringBuilder();

        for (var dr = r1; dr <= r2; dr++)
        {
            var virtualLine = scrollbackCount - _scrollOffset + dr;
            if (virtualLine < 0) continue;

            Cell[]? row = null;
            if (virtualLine < scrollbackCount)
            {
                row = _emulator.GetScrollbackLine(virtualLine);
            }
            else
            {
                var snap = _emulator.GetScreenSnapshot();
                var screenRow = virtualLine - scrollbackCount;
                if (screenRow < _emulator.Rows)
                {
                    row = new Cell[_emulator.Cols];
                    for (var c = 0; c < _emulator.Cols; c++)
                        row[c] = snap[screenRow, c];
                }
            }

            if (row is null) continue;

            var colStart = dr == r1 ? c1 : 0;
            var colEnd   = dr == r2 ? c2 : _emulator.Cols - 1;

            for (var c = colStart; c <= Math.Min(colEnd, row.Length - 1); c++)
            {
                var cell = row[c];
                if (cell.IsContinuation) continue;
                sb.Append(cell.IsBlank ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
            }

            if (dr < r2)
            {
                // Trim trailing spaces per line
                while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ─── Color mapping ────────────────────────────────────────────────────────

    private static readonly ISolidColorBrush s_selectionBrush =
        new SolidColorBrush(Color.FromRgb(0x26, 0x4f, 0x78));

    internal static ISolidColorBrush BrushForColor(TerminalColor color, bool isForeground) =>
        color.Kind switch
        {
            ColorKind.Default    => isForeground
                ? new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc))
                : Brushes.Transparent,
            ColorKind.Named      => new SolidColorBrush(Named16[color.Index % 16]),
            ColorKind.Indexed    => color.Index < 16
                ? new SolidColorBrush(Named16[color.Index])
                : color.Index < 232
                    ? XTerm256(color.Index)
                    : GrayscaleRamp(color.Index),
            ColorKind.TrueColor  => new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            _ => Brushes.White,
        };

    internal static SolidColorBrush XTerm256(byte idx)
    {
        var i = idx - 16;
        var b = i % 6; var g = (i / 6) % 6; var r = i / 36;
        byte c(int v) => v == 0 ? (byte)0 : (byte)(55 + v * 40);
        return new SolidColorBrush(Color.FromRgb(c(r), c(g), c(b)));
    }

    internal static SolidColorBrush GrayscaleRamp(byte idx)
    {
        var v = (byte)(8 + (idx - 232) * 10);
        return new SolidColorBrush(Color.FromRgb(v, v, v));
    }

    internal static readonly Color[] Named16 =
    [
        Color.FromRgb(0,0,0),       Color.FromRgb(170,0,0),
        Color.FromRgb(0,170,0),     Color.FromRgb(170,170,0),
        Color.FromRgb(0,0,170),     Color.FromRgb(170,0,170),
        Color.FromRgb(0,170,170),   Color.FromRgb(170,170,170),
        Color.FromRgb(85,85,85),    Color.FromRgb(255,85,85),
        Color.FromRgb(85,255,85),   Color.FromRgb(255,255,85),
        Color.FromRgb(85,85,255),   Color.FromRgb(255,85,255),
        Color.FromRgb(85,255,255),  Color.FromRgb(255,255,255),
    ];

    // ─── Key mapping ──────────────────────────────────────────────────────────

    /// <param name="applicationCursorKeys">
    /// DECCKM state (<see cref="ITerminalEmulator.CursorKeyApplicationMode"/>) — when true, the four
    /// arrow keys encode as application sequences (<c>ESC O A/B/C/D</c>) instead of normal-mode ones
    /// (<c>ESC [ A/B/C/D</c>), matching what full-screen programs like vim/less expect once they switch
    /// into application mode (issue #75).
    /// </param>
    internal static byte[]? KeyToBytes(KeyEventArgs e, bool applicationCursorKeys = false) => e.Key switch
    {
        Key.Up       => (applicationCursorKeys ? "\x1bOA"u8 : "\x1b[A"u8).ToArray(),
        Key.Down     => (applicationCursorKeys ? "\x1bOB"u8 : "\x1b[B"u8).ToArray(),
        Key.Right    => (applicationCursorKeys ? "\x1bOC"u8 : "\x1b[C"u8).ToArray(),
        Key.Left     => (applicationCursorKeys ? "\x1bOD"u8 : "\x1b[D"u8).ToArray(),
        Key.Home     => "\x1b[H"u8.ToArray(),
        Key.End      => "\x1b[F"u8.ToArray(),
        Key.Insert   => "\x1b[2~"u8.ToArray(),
        Key.Delete   => "\x1b[3~"u8.ToArray(),
        Key.PageUp   => "\x1b[5~"u8.ToArray(),
        Key.PageDown => "\x1b[6~"u8.ToArray(),
        Key.F1       => "\x1bOP"u8.ToArray(),
        Key.F2       => "\x1bOQ"u8.ToArray(),
        Key.F3       => "\x1bOR"u8.ToArray(),
        Key.F4       => "\x1bOS"u8.ToArray(),
        Key.F5       => "\x1b[15~"u8.ToArray(),
        Key.F6       => "\x1b[17~"u8.ToArray(),
        Key.F7       => "\x1b[18~"u8.ToArray(),
        Key.F8       => "\x1b[19~"u8.ToArray(),
        Key.F9       => "\x1b[20~"u8.ToArray(),
        Key.F10      => "\x1b[21~"u8.ToArray(),
        Key.F11      => "\x1b[23~"u8.ToArray(),
        Key.F12      => "\x1b[24~"u8.ToArray(),
        Key.Escape   => "\x1b"u8.ToArray(),
        Key.Tab      => "\t"u8.ToArray(),
        Key.Return   => "\r"u8.ToArray(),
        Key.Back     => "\x7f"u8.ToArray(),
        _ => null,
    };
}
