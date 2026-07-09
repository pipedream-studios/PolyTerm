using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    // Text selection: view-space (col, row) coordinates, null = no selection
    private (int Col, int Row)? _selStart;
    private (int Col, int Row)? _selEnd;
    private bool _selecting;

    public TerminalSurface()
    {
        Focusable = true;
        ClipToBounds = true;

        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (_pendingRender) { _pendingRender = false; InvalidateVisual(); } });
    }

    // ─── Visual tree lifecycle ────────────────────────────────────────────────

    /// <summary>Starts the coalesced render timer and requests focus once the control is loaded.</summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Stops the render timer and unsubscribes from the attached channel, if any, to avoid leaking the event handler.</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderTimer.Stop();
        if (_attachedChannel is not null)
        {
            _attachedChannel.OutputReceived -= OnOutput;
            _attachedChannel = null;
        }
    }

    /// <summary>Reattaches to a new <see cref="Channel"/> when it changes, and re-measures cell size when the font or font size changes.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChannelProperty)
            Reattach(change.NewValue as ITerminalChannel);

        if (change.Property == FontSizeProperty || change.Property == TerminalFontProperty)
            MeasureCell();
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
        if (_attachedChannel is not null)
            _attachedChannel.OutputReceived -= OnOutput;

        _attachedChannel = channel;
        _scrollOffset = 0;
        _selStart = null;
        _selEnd = null;

        if (channel is not null)
        {
            var (cols, rows) = GridSize();
            _emulator = new TerminalEmulator(cols, rows, 5000,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalEmulator>.Instance);
            _emulator.ScreenUpdated += (_, _) => { _pendingRender = true; };
            _emulator.TitleChanged  += OnEmulatorTitleChanged;
            channel.OutputReceived  += OnOutput;
        }
        else
        {
            _emulator = null;
            Dispatcher.UIThread.Post(() => EmulatorTitle = "");
        }

        InvalidateVisual();
    }

    private void OnOutput(object? sender, ReadOnlyMemory<byte> data)
    {
        _scrollOffset = 0; // snap to bottom on new output
        _emulator?.Feed(data.Span);
        // Render triggered by ScreenUpdated → timer
    }

    private void OnEmulatorTitleChanged(object? sender, string title)
    {
        // TitleChanged fires on the thread that called Feed (typically a background read loop).
        // EmulatorTitle is a StyledProperty, so it must be set on the UI thread.
        Dispatcher.UIThread.Post(() => EmulatorTitle = title);
    }

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
        _scrollOffset = 0;
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

        // Ctrl+letter → control characters ^A (0x01) … ^Z (0x1A)
        // Require ONLY Ctrl (not Ctrl+Shift) to avoid stealing Ctrl+Shift+C/V above
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.A and <= Key.Z)
        {
            e.Handled = true;
            _scrollOffset = 0;
            var ctrl = (byte)((int)e.Key - (int)Key.A + 1);
            _ = _attachedChannel.SendInputAsync(new[] { ctrl }).AsTask();
            return;
        }

        var bytes = KeyToBytes(e);
        if (bytes is null) return;
        e.Handled = true;
        _scrollOffset = 0;
        _ = _attachedChannel.SendInputAsync(bytes).AsTask();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_emulator is null || _cellWidth == 0) return;
        var cols = Math.Max(1, (int)(e.NewSize.Width / _cellWidth));
        var rows = Math.Max(1, (int)(e.NewSize.Height / _cellHeight));
        _emulator.Resize(cols, rows);
        _ = _attachedChannel?.ResizeAsync(cols, rows).AsTask();
    }

    // ─── Mouse input ──────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_emulator is null) return;

        var lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * 3));
        if (e.Delta.Y > 0)
            _scrollOffset = Math.Min(_scrollOffset + lines, _emulator.ScrollbackLineCount);
        else
            _scrollOffset = Math.Max(_scrollOffset - lines, 0);

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
        // Single click (no drag) = clear selection
        if (_selStart == _selEnd) { _selStart = null; _selEnd = null; _pendingRender = true; }
    }

    private (int Col, int Row) HitTest(Point p) =>
        (Math.Clamp((int)(p.X / _cellWidth),  0, (_emulator?.Cols ?? 1) - 1),
         Math.Clamp((int)(p.Y / _cellHeight), 0, (_emulator?.Rows ?? 1) - 1));

    // ─── Clipboard ────────────────────────────────────────────────────────────

    private async Task PasteFromClipboardAsync()
    {
        var text = await (TopLevel.GetTopLevel(this)?.Clipboard?.GetTextAsync()
            ?? Task.FromResult<string?>(null)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text) || _attachedChannel is null) return;
        _scrollOffset = 0;
        await _attachedChannel.SendInputAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
    }

    // ─── Render ───────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), new Rect(Bounds.Size));

        if (_emulator is null) return;

        MeasureCell();
        var typeface = new Typeface(TerminalFont);
        var scrollbackCount = _emulator.ScrollbackLineCount;
        var isScrolled = _scrollOffset > 0;

        // Only allocate a screen snapshot when needed (some screen rows may be visible)
        Cell[,]? screenSnap = null;
        var firstScreenDisplayRow = _scrollOffset > scrollbackCount ? _emulator.Rows : scrollbackCount - _scrollOffset;
        if (firstScreenDisplayRow < _emulator.Rows)
            screenSnap = _emulator.GetScreenSnapshot();

        for (var displayRow = 0; displayRow < _emulator.Rows; displayRow++)
        {
            var virtualLine = scrollbackCount - _scrollOffset + displayRow;
            if (virtualLine < 0) continue;

            for (var col = 0; col < _emulator.Cols; col++)
            {
                Cell cell;
                if (virtualLine < scrollbackCount)
                {
                    var sbRow = _emulator.GetScrollbackLine(virtualLine);
                    cell = col < sbRow.Length ? sbRow[col] : default;
                }
                else if (screenSnap is not null)
                {
                    var screenRow = virtualLine - scrollbackCount;
                    cell = screenSnap[screenRow, col];
                }
                else continue;

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
        if (!isScrolled && _emulator.CursorVisible)
        {
            var cx = _emulator.CursorCol * _cellWidth;
            var cy = _emulator.CursorRow * _cellHeight;
            context.DrawRectangle(Brushes.White, null, new Rect(cx, cy + _cellHeight - 2, _cellWidth, 2));
        }

        // Scrollback indicator
        if (isScrolled)
        {
            const string hint = "Scroll: ↑↓ wheel  •  Copy: Ctrl+Shift+C";
            var ft = new FormattedText($"↑ {_scrollOffset} lines  {hint}",
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

    private static ISolidColorBrush BrushForColor(TerminalColor color, bool isForeground) =>
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

    private static SolidColorBrush XTerm256(byte idx)
    {
        var i = idx - 16;
        var b = i % 6; var g = (i / 6) % 6; var r = i / 36;
        byte c(int v) => v == 0 ? (byte)0 : (byte)(55 + v * 40);
        return new SolidColorBrush(Color.FromRgb(c(r), c(g), c(b)));
    }

    private static SolidColorBrush GrayscaleRamp(byte idx)
    {
        var v = (byte)(8 + (idx - 232) * 10);
        return new SolidColorBrush(Color.FromRgb(v, v, v));
    }

    private static readonly Color[] Named16 =
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

    private static byte[]? KeyToBytes(KeyEventArgs e) => e.Key switch
    {
        Key.Up       => "\x1b[A"u8.ToArray(),
        Key.Down     => "\x1b[B"u8.ToArray(),
        Key.Right    => "\x1b[C"u8.ToArray(),
        Key.Left     => "\x1b[D"u8.ToArray(),
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
