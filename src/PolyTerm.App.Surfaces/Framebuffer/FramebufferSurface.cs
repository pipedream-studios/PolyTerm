using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PolyTerm.Engine;

namespace PolyTerm.App.Surfaces.Framebuffer;

/// <summary>
/// Avalonia custom control that renders an <see cref="IFramebufferChannel"/>'s pixel updates into a
/// <see cref="WriteableBitmap"/>, and translates pointer/keyboard input back into
/// <see cref="PointerEvent"/>/<see cref="KeyEvent"/> calls. First real consumer of
/// <see cref="SurfaceKind.Framebuffer"/> (VNC today) — same overall shape as
/// <see cref="Terminal.TerminalSurface"/> (owns rendering, translates input), not its content.
/// </summary>
public sealed class FramebufferSurface : Control, IDisposable
{
    public static readonly StyledProperty<IFramebufferChannel?> ChannelProperty =
        AvaloniaProperty.Register<FramebufferSurface, IFramebufferChannel?>(nameof(Channel));

    public IFramebufferChannel? Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public static readonly StyledProperty<ClipboardSync?> ClipboardSyncProperty =
        AvaloniaProperty.Register<FramebufferSurface, ClipboardSync?>(nameof(ClipboardSync));

    /// <summary>The session's clipboard sync, or null if it doesn't share the clipboard.</summary>
    public ClipboardSync? ClipboardSync
    {
        get => GetValue(ClipboardSyncProperty);
        set => SetValue(ClipboardSyncProperty, value);
    }

    /// <summary>
    /// How often the local clipboard is checked while this view has focus. Avalonia has no event for it
    /// changing, and another program can change it without taking focus away.
    /// </summary>
    private static readonly TimeSpan ClipboardCheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _clipboardTimer;
    private IFramebufferChannel? _attachedChannel;
    private WriteableBitmap? _bitmap;
    private readonly object _bitmapLock = new();
    private volatile bool _pendingRender;
    private readonly DispatcherTimer _renderTimer;

    private readonly HeldInput _held = new();
    private (int X, int Y) _lastRemotePointer;
    private WindowBase? _window;

    public FramebufferSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (_pendingRender) { _pendingRender = false; InvalidateVisual(); } });
        // Not the constructor that takes a callback, which also starts the timer.
        _clipboardTimer = new DispatcherTimer { Interval = ClipboardCheckInterval };
        _clipboardTimer.Tick += (_, _) => SendLocalClipboard();
    }

    // ─── Visual tree lifecycle ────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
        // The surface keeps focus inside a window that isn't active, so losing focus doesn't cover this.
        _window = TopLevel.GetTopLevel(this) as WindowBase;
        if (_window is not null)
        {
            _window.Activated += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
        }
        ConnectClipboard();
        _clipboardTimer.Start();
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderTimer.Stop();
        _clipboardTimer.Stop();
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
        ReleaseHeldInput();
        Detach();
        Dispose();
    }

    /// <summary>Releases the current <see cref="WriteableBitmap"/>, if any. Safe to call more than once — a later <see cref="BlitAndInvalidate"/> just recreates it.</summary>
    public void Dispose()
    {
        lock (_bitmapLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChannelProperty)
            Reattach(change.NewValue as IFramebufferChannel);
        else if (change.Property == ClipboardSyncProperty)
            ConnectClipboard();
    }

    // ─── Clipboard (issue #391) ───────────────────────────────────────────────

    /// <summary>Gives the session's clipboard sync this window's clipboard, once there are both.</summary>
    private void ConnectClipboard()
    {
        if (ClipboardSync is not { } sync || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        sync.UseClipboard(new AvaloniaLocalClipboard(clipboard));
        SendLocalClipboard();
    }

    /// <summary>
    /// Offers the local clipboard to the remote, but only while this view has focus in the active window:
    /// what's copied while working elsewhere isn't shared until the user comes back.
    /// </summary>
    private void SendLocalClipboard()
    {
        if (ClipboardSync is { } sync && IsFocused && _window is { IsActive: true })
            _ = sync.SendLocalAsync();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        SendLocalClipboard();
    }

    private void OnWindowActivated(object? sender, EventArgs e) => SendLocalClipboard();

    private void Reattach(IFramebufferChannel? channel)
    {
        ReleaseHeldInput(); // to the old channel, which is the one that saw the presses
        Detach();
        _attachedChannel = channel;
        if (channel is not null)
            channel.RegionUpdated += OnRegionUpdated;
    }

    private void Detach()
    {
        if (_attachedChannel is not null)
        {
            _attachedChannel.RegionUpdated -= OnRegionUpdated;
            _attachedChannel = null;
        }
    }

    // ─── Rendering ────────────────────────────────────────────────────────────

    // Raised on an engine thread — marshal to the UI thread before touching the bitmap.
    private void OnRegionUpdated(object? sender, FramebufferRegionUpdatedEventArgs e) =>
        Dispatcher.UIThread.Post(() => BlitAndInvalidate(e));

    private void BlitAndInvalidate(FramebufferRegionUpdatedEventArgs e)
    {
        if (!MemoryMarshal.TryGetArray(e.Data, out var segment) || segment.Array is null)
            return; // shouldn't happen given how VncFramebufferChannel/RdpFreerdpFramebufferChannel construct Data — be defensive

        var channel = _attachedChannel;
        if (channel is null) return; // detached between the event being raised and this UI-thread callback running

        // The bitmap always matches the CHANNEL's full frame size, not this specific region's size —
        // VNC currently always sends region == full frame (its underlying library has no dirty-rect
        // tracking - see VncFramebufferChannel's own comment), but RDP now sends real sub-rectangles,
        // and blitting one at row 0 regardless of e.X/e.Y (the previous behavior here) would have
        // silently overwritten only the top-left corner with garbage-positioned pixel data.
        var fullWidth = Math.Max(1, channel.Width);
        var fullHeight = Math.Max(1, channel.Height);

        lock (_bitmapLock)
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != fullWidth || _bitmap.PixelSize.Height != fullHeight)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(fullWidth, fullHeight),
                    new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            }

            using var fb = _bitmap.Lock();
            var srcStride = e.Width * 4;
            var dstColumnOffset = e.X * 4;
            for (var row = 0; row < e.Height; row++)
            {
                var dstY = e.Y + row;
                if (dstY < 0 || dstY >= fullHeight) continue; // defensive - a well-behaved channel never sends an out-of-bounds region
                Marshal.Copy(segment.Array, segment.Offset + row * srcStride,
                    fb.Address + dstY * fb.RowBytes + dstColumnOffset, srcStride);
            }
        }

        _pendingRender = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        WriteableBitmap? bitmap;
        lock (_bitmapLock) bitmap = _bitmap;
        if (bitmap is null) return;
        context.DrawImage(bitmap, new Rect(bitmap.Size), Bounds);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_attachedChannel is null) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width  = (int)(e.NewSize.Width * scaling);
        var height = (int)(e.NewSize.Height * scaling);
        if (width < 1 || height < 1) return;
        _ = _attachedChannel.ResizeAsync(width, height).AsTask();
    }

    // ─── Pointer input ────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        ReleaseStaleModifiers(e.KeyModifiers); // first, or the remote reads the click as e.g. Super+click
        SendPointer(e.GetPosition(this), ToPointerButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind), PointerEventKind.Press);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleaseStaleModifiers(e.KeyModifiers);
        SendPointer(e.GetPosition(this), ToPointerButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind), PointerEventKind.Release);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ReleaseStaleModifiers(e.KeyModifiers);
        SendPointer(e.GetPosition(this), PointerButton.None, PointerEventKind.Move);
    }

    /// <summary>Also raised after every ordinary release, by which point the button is no longer held.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ReleaseHeldButtons();
    }

    /// <summary>VNC has no dedicated wheel message — a wheel tick is sent as an immediate press+release of the wheel "button", the conventional RFB approach.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ReleaseStaleModifiers(e.KeyModifiers);
        var button = e.Delta.Y > 0 ? PointerButton.WheelUp : PointerButton.WheelDown;
        var pos = e.GetPosition(this);
        SendPointer(pos, button, PointerEventKind.Press);
        SendPointer(pos, button, PointerEventKind.Release);
        e.Handled = true;
    }

    private void SendPointer(Point p, PointerButton button, PointerEventKind kind)
    {
        var channel = _attachedChannel;
        if (channel is null) return;
        var (x, y) = ToRemoteCoords(p, channel);
        _lastRemotePointer = (x, y);
        if (button is PointerButton.Left or PointerButton.Right or PointerButton.Middle)
        {
            if (kind == PointerEventKind.Press) _held.Press(button);
            else if (kind == PointerEventKind.Release) _held.Release(button);
        }
        _ = channel.SendPointerAsync(new PointerEvent { X = x, Y = y, Button = button, Kind = kind }).AsTask();
    }

    private (int X, int Y) ToRemoteCoords(Point p, IFramebufferChannel channel) =>
        ComputeRemoteCoords(p.X, p.Y, Bounds.Width, Bounds.Height, channel.Width, channel.Height);

    /// <summary>Pure coordinate-scaling/clamping core of <see cref="ToRemoteCoords"/>, extracted (not just made <c>internal</c>, since the original took a live <see cref="Point"/>/<see cref="IFramebufferChannel"/>) so it's unit-testable without a real control/attached channel.</summary>
    internal static (int X, int Y) ComputeRemoteCoords(double pointerX, double pointerY, double boundsWidth, double boundsHeight, int channelWidth, int channelHeight)
    {
        if (boundsWidth <= 0 || boundsHeight <= 0 || channelWidth <= 0 || channelHeight <= 0)
            return (0, 0);
        var x = (int)(pointerX / boundsWidth * channelWidth);
        var y = (int)(pointerY / boundsHeight * channelHeight);
        return (Math.Clamp(x, 0, channelWidth - 1), Math.Clamp(y, 0, channelHeight - 1));
    }

    internal static PointerButton ToPointerButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased     => PointerButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased   => PointerButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => PointerButton.Middle,
        _ => PointerButton.None,
    };

    // ─── Keyboard input ───────────────────────────────────────────────────────

    /// <summary>
    /// Printable characters go through here as an immediate press+release pair — X11 keysyms in the
    /// Latin-1 range (0x00-0xFF) map directly to the character's code point, and beyond that the X11
    /// Unicode keysym convention is <c>0x01000000 + code point</c> (see the X11 keysymdef.h spec).
    /// Non-printable/control keys are handled separately in <see cref="OnKeyDown"/>/<see cref="OnKeyUp"/>
    /// so modifier-held combinations (Ctrl+C etc., which don't raise TextInput) still work.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        var channel = _attachedChannel;
        if (channel is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;

        foreach (var c in e.Text)
        {
            var symbol = CharToKeySymbol(c);
            _ = channel.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = KeyEventKind.Press }).AsTask();
            _ = channel.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = KeyEventKind.Release }).AsTask();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Not for a modifier's own press: X11 reports the state from before it, so it'd look unheld.
        // This runs for every other key, mapped or not, ahead of any text it goes on to type.
        if (HeldInput.ModifierFor(e.Key) == KeyModifiers.None)
            ReleaseStaleModifiers(e.KeyModifiers);
        SendControlKey(e.Key, e.KeyModifiers, KeyEventKind.Press, e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        SendControlKey(e.Key, e.KeyModifiers, KeyEventKind.Release, e);
        // macOS raises one key-up for a pair of modifier keys, once both are up, and names only the
        // last. With both Shifts down, for example, this is where the other one gets released.
        if (HeldInput.ModifierFor(e.Key) != KeyModifiers.None)
            ReleaseStaleModifiers(e.KeyModifiers);
    }

    private void SendControlKey(Key key, KeyModifiers modifiers, KeyEventKind kind, RoutedEventArgs e)
    {
        var channel = _attachedChannel;
        if (channel is null || !ControlKeyMap.TryGetValue(key, out var symbol)) return;
        e.Handled = true;
        if (kind == KeyEventKind.Press) _held.Press(key);
        else _held.Release(key);
        _ = channel.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = kind, Modifiers = ToModifiers(modifiers) }).AsTask();
    }

    // ─── Releasing held input (issue #368) ────────────────────────────────────

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseHeldInput();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => ReleaseHeldInput();

    /// <summary>Releases everything the remote still thinks is down, for when the real releases may not reach this surface.</summary>
    private void ReleaseHeldInput()
    {
        ReleaseHeldButtons(); // buttons before keys, so a modified drag ends still modified
        ReleaseKeys(_held.TakeKeys());
    }

    /// <summary>Releases modifiers that were let go without a key-up reaching this surface, e.g. one taken by a system shortcut.</summary>
    private void ReleaseStaleModifiers(KeyModifiers reported) =>
        ReleaseKeys(_held.TakeModifiersNotDown(reported, HeldInput.ReliableModifiers(OperatingSystem.IsMacOS())));

    private void ReleaseKeys(IReadOnlyList<Key> keys)
    {
        var channel = _attachedChannel;
        if (channel is null) return;
        foreach (var key in keys)
            _ = channel.SendKeyAsync(new KeyEvent { KeySymbol = ControlKeyMap[key], Kind = KeyEventKind.Release }).AsTask();
    }

    private void ReleaseHeldButtons()
    {
        var buttons = _held.TakeButtons();
        var channel = _attachedChannel;
        if (channel is null) return;
        var (x, y) = _lastRemotePointer;
        foreach (var button in buttons)
            _ = channel.SendPointerAsync(new PointerEvent { X = x, Y = y, Button = button, Kind = PointerEventKind.Release }).AsTask();
    }

    internal static string CharToKeySymbol(char c) =>
        ((int)(c <= 0xFF ? c : 0x01000000 + c)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Non-printable/control keys only — printable characters go through <see cref="OnTextInput"/>
    /// instead. Verified against Avalonia 11.2.7's actual <see cref="Key"/> enum: <c>Return</c>/<c>Enter</c>,
    /// <c>PageUp</c>/<c>Prior</c>, <c>PageDown</c>/<c>Next</c>, and <c>PrintScreen</c>/<c>Snapshot</c> are
    /// aliases sharing the same underlying value — only one of each pair is listed here, since a
    /// dictionary initializer with both throws at class-load time.
    /// </summary>
    internal static readonly Dictionary<Key, string> ControlKeyMap = new()
    {
        [Key.Enter] = "Return",
        [Key.Back] = "BackSpace",
        [Key.Escape] = "Escape",
        [Key.Tab] = "Tab",
        [Key.Up] = "Up", [Key.Down] = "Down", [Key.Left] = "Left", [Key.Right] = "Right",
        [Key.Home] = "Home", [Key.End] = "End",
        [Key.PageUp] = "Prior", [Key.PageDown] = "Next",
        [Key.Insert] = "Insert", [Key.Delete] = "Delete",
        [Key.LeftShift] = "Shift_L", [Key.RightShift] = "Shift_R",
        [Key.LeftCtrl] = "Control_L", [Key.RightCtrl] = "Control_R",
        [Key.LeftAlt] = "Alt_L", [Key.RightAlt] = "Alt_R",
        [Key.LWin] = "Super_L", [Key.RWin] = "Super_R",
        [Key.CapsLock] = "Caps_Lock", [Key.NumLock] = "Num_Lock", [Key.Scroll] = "Scroll_Lock",
        [Key.F1] = "F1", [Key.F2] = "F2", [Key.F3] = "F3", [Key.F4] = "F4",
        [Key.F5] = "F5", [Key.F6] = "F6", [Key.F7] = "F7", [Key.F8] = "F8",
        [Key.F9] = "F9", [Key.F10] = "F10", [Key.F11] = "F11", [Key.F12] = "F12",
        [Key.PrintScreen] = "Print", [Key.Pause] = "Pause",
    };

    internal static ModifierKeys ToModifiers(KeyModifiers mods)
    {
        var result = ModifierKeys.None;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= ModifierKeys.Shift;
        if (mods.HasFlag(KeyModifiers.Control)) result |= ModifierKeys.Control;
        if (mods.HasFlag(KeyModifiers.Alt)) result |= ModifierKeys.Alt;
        if (mods.HasFlag(KeyModifiers.Meta)) result |= ModifierKeys.Meta;
        return result;
    }
}
