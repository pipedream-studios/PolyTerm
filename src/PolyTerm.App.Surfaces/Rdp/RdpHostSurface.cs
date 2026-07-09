using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using PolyTerm.Engine;

namespace PolyTerm.App.Surfaces.Rdp;

/// <summary>
/// Embeds a <see cref="SurfaceKind.NativeHost"/> session's native window (RDP's MSTSC ActiveX
/// control today) inside the Avalonia visual tree. Avalonia's <see cref="NativeControlHost"/> needs
/// a valid child <see cref="IPlatformHandle"/> synchronously as soon as this control attaches to the
/// visual tree — long before the protocol module has actually connected and created its own window —
/// so this creates a plain Win32 placeholder child window up front and reparents the real native
/// window into it once <see cref="INativeHostChannel.HandleChanged"/> fires.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RdpHostSurface : NativeControlHost
{
    /// <summary>Backs <see cref="Channel"/>.</summary>
    public static readonly StyledProperty<INativeHostChannel?> ChannelProperty =
        AvaloniaProperty.Register<RdpHostSurface, INativeHostChannel?>(nameof(Channel));

    /// <summary>The native-host channel whose window this surface embeds and keeps sized to its own bounds.</summary>
    public INativeHostChannel? Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    private nint _placeholderHwnd;
    private INativeHostChannel? _subscribed;
    private Avalonia.Threading.DispatcherTimer? _resizeDebounce;

    static RdpHostSurface()
    {
        BoundsProperty.Changed.AddClassHandler<RdpHostSurface>((c, _) => c.SyncChildBounds());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var size = PhysicalSize();
        _placeholderHwnd = NativeMethods.CreateChildWindow(parent.Handle,
            Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height));

        AttachChannel(Channel);
        return new PlatformHandle(_placeholderHwnd, "HWND");
    }

    /// <summary>
    /// <see cref="Visual.Bounds"/> is in device-independent units (96 DPI baseline); the raw Win32
    /// calls below need actual physical pixels. Without this scaling, the embedded control renders
    /// correctly-shaped but undersized on any display above 100% scaling (125%/150% are common) —
    /// confirmed live: the desktop rendered and resized, just consistently smaller than the tab.
    /// </summary>
    private Size PhysicalSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new Size(Bounds.Width * scaling, Bounds.Height * scaling);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _resizeDebounce?.Stop();
        AttachChannel(null);
        NativeMethods.DestroyChildWindow(_placeholderHwnd);
        _placeholderHwnd = 0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChannelProperty && _placeholderHwnd != 0)
            AttachChannel(change.NewValue as INativeHostChannel);
    }

    /// <summary>
    /// Unsubscribes from any previously-attached channel and subscribes to <paramref name="channel"/>,
    /// embedding its native window immediately if it's already available (reconnect case) rather than
    /// only reacting to future <see cref="INativeHostChannel.HandleChanged"/> events.
    /// </summary>
    private void AttachChannel(INativeHostChannel? channel)
    {
        if (_subscribed is not null)
            _subscribed.HandleChanged -= OnChannelHandleChanged;
        _subscribed = channel;
        if (channel is not null)
            channel.HandleChanged += OnChannelHandleChanged;

        if (channel is { Handle: not 0 } && _placeholderHwnd != 0)
            EmbedAndRenegotiate(channel);
    }

    private void OnChannelHandleChanged(object? sender, EventArgs e)
    {
        if (Channel is { Handle: not 0 } channel && _placeholderHwnd != 0)
            EmbedAndRenegotiate(channel);
    }

    private void SyncChildBounds()
    {
        if (Channel is { Handle: not 0 } channel && _placeholderHwnd != 0)
        {
            NativeMethods.Resize(channel.Handle, PhysicalSize());
            ScheduleResolutionRenegotiate();
        }
    }

    /// <summary>
    /// Reparents/resizes the raw window (immediate — the win32-level bounds), then schedules a
    /// debounced RDP-level resolution renegotiation (<see cref="INativeHostChannel.ResizeAsync"/>).
    /// Both are needed: the raw resize alone leaves <c>SmartSizing</c> aspect-ratio-scaling a desktop
    /// still negotiated at the connect-time default, which is what "not filling the window" (gray
    /// bars) turned out to mean on click-test — the actual RDP desktop resolution has to change too.
    /// </summary>
    private void EmbedAndRenegotiate(INativeHostChannel channel)
    {
        NativeMethods.Embed(channel.Handle, _placeholderHwnd, PhysicalSize());
        ScheduleResolutionRenegotiate();
    }

    /// <summary>
    /// Debounces <see cref="INativeHostChannel.ResizeAsync"/> (an RDP <c>Reconnect</c> under the
    /// hood) so a window-resize drag, which fires many rapid bounds-changed events, doesn't spam
    /// reconnects — only the size 400ms after the last change actually gets requested.
    /// </summary>
    private void ScheduleResolutionRenegotiate()
    {
        _resizeDebounce ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _resizeDebounce.Stop();
        _resizeDebounce.Tick -= OnResizeDebounceTick;
        _resizeDebounce.Tick += OnResizeDebounceTick;
        _resizeDebounce.Start();
    }

    private async void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounce!.Stop();
        var channel = Channel;
        var size = PhysicalSize();
        if (channel is not { Handle: not 0 } || size.Width < 1 || size.Height < 1)
            return;

        // async void: an unhandled exception here would crash the whole app, not just this tab.
        // INativeHostChannel.ResizeAsync's RDP implementation already swallows the expected
        // "server doesn't support this" failure, but this is the last line of defense regardless.
        try { await channel.ResizeAsync((int)size.Width, (int)size.Height); }
        catch { /* best-effort resize; a failure here shouldn't tear down the tab */ }
    }

    private static class NativeMethods
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int SWP_NOZORDER = 0x0004;

        public static nint CreateChildWindow(nint parent, int width, int height) =>
            CreateWindowEx(0, "STATIC", "", WS_CHILD | WS_VISIBLE,
                0, 0, width, height, parent, 0, 0, 0);

        public static void DestroyChildWindow(nint hwnd)
        {
            if (hwnd != 0) DestroyWindow(hwnd);
        }

        /// <summary>Reparents the protocol module's native window under our placeholder and sizes it to fill it.</summary>
        public static void Embed(nint childHwnd, nint placeholderHwnd, Size size)
        {
            SetParent(childHwnd, placeholderHwnd);
            Resize(childHwnd, size);
        }

        public static void Resize(nint hwnd, Size size) =>
            SetWindowPos(hwnd, 0, 0, 0, Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height), SWP_NOZORDER);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, nint parent, nint menu, nint hInstance, nint param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, int flags);
    }
}
