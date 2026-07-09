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

    private IFramebufferChannel? _attachedChannel;
    private WriteableBitmap? _bitmap;
    private readonly object _bitmapLock = new();
    private volatile bool _pendingRender;
    private readonly DispatcherTimer _renderTimer;

    public FramebufferSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (_pendingRender) { _pendingRender = false; InvalidateVisual(); } });
    }

    // ─── Visual tree lifecycle ────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderTimer.Stop();
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
    }

    private void Reattach(IFramebufferChannel? channel)
    {
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
            return; // shouldn't happen given how VncFramebufferChannel constructs Data — be defensive

        lock (_bitmapLock)
        {
            if (_bitmap is null || _bitmap.PixelSize.Width != e.Width || _bitmap.PixelSize.Height != e.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(Math.Max(1, e.Width), Math.Max(1, e.Height)),
                    new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            }

            using var fb = _bitmap.Lock();
            var srcStride = e.Width * 4;
            for (var row = 0; row < e.Height; row++)
                Marshal.Copy(segment.Array, segment.Offset + row * srcStride, fb.Address + row * fb.RowBytes, srcStride);
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
        SendPointer(e.GetPosition(this), ToPointerButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind), PointerEventKind.Press);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SendPointer(e.GetPosition(this), ToPointerButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind), PointerEventKind.Release);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SendPointer(e.GetPosition(this), PointerButton.None, PointerEventKind.Move);
    }

    /// <summary>VNC has no dedicated wheel message — a wheel tick is sent as an immediate press+release of the wheel "button", the conventional RFB approach.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
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
        _ = channel.SendPointerAsync(new PointerEvent { X = x, Y = y, Button = button, Kind = kind }).AsTask();
    }

    private (int X, int Y) ToRemoteCoords(Point p, IFramebufferChannel channel)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || channel.Width <= 0 || channel.Height <= 0)
            return (0, 0);
        var x = (int)(p.X / Bounds.Width * channel.Width);
        var y = (int)(p.Y / Bounds.Height * channel.Height);
        return (Math.Clamp(x, 0, channel.Width - 1), Math.Clamp(y, 0, channel.Height - 1));
    }

    private static PointerButton ToPointerButton(PointerUpdateKind kind) => kind switch
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
        SendControlKey(e.Key, e.KeyModifiers, KeyEventKind.Press, e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        SendControlKey(e.Key, e.KeyModifiers, KeyEventKind.Release, e);
    }

    private void SendControlKey(Key key, KeyModifiers modifiers, KeyEventKind kind, RoutedEventArgs e)
    {
        var channel = _attachedChannel;
        if (channel is null || !ControlKeyMap.TryGetValue(key, out var symbol)) return;
        e.Handled = true;
        _ = channel.SendKeyAsync(new KeyEvent { KeySymbol = symbol, Kind = kind, Modifiers = ToModifiers(modifiers) }).AsTask();
    }

    private static string CharToKeySymbol(char c) =>
        ((int)(c <= 0xFF ? c : 0x01000000 + c)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Non-printable/control keys only — printable characters go through <see cref="OnTextInput"/>
    /// instead. Verified against Avalonia 11.2.7's actual <see cref="Key"/> enum: <c>Return</c>/<c>Enter</c>,
    /// <c>PageUp</c>/<c>Prior</c>, <c>PageDown</c>/<c>Next</c>, and <c>PrintScreen</c>/<c>Snapshot</c> are
    /// aliases sharing the same underlying value — only one of each pair is listed here, since a
    /// dictionary initializer with both throws at class-load time.
    /// </summary>
    private static readonly Dictionary<Key, string> ControlKeyMap = new()
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

    private static ModifierKeys ToModifiers(KeyModifiers mods)
    {
        var result = ModifierKeys.None;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= ModifierKeys.Shift;
        if (mods.HasFlag(KeyModifiers.Control)) result |= ModifierKeys.Control;
        if (mods.HasFlag(KeyModifiers.Alt)) result |= ModifierKeys.Alt;
        if (mods.HasFlag(KeyModifiers.Meta)) result |= ModifierKeys.Meta;
        return result;
    }
}
