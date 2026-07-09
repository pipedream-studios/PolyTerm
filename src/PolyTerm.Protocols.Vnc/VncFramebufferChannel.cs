using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Rendering;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Vnc;

/// <summary>
/// Bridges <see cref="MarcusW.VncClient"/>'s <see cref="IRenderTarget"/> (the library's own pixel-buffer
/// callback interface) to our <see cref="IFramebufferChannel"/>. Deliberately reimplemented from scratch
/// rather than reusing the library's own <c>MarcusW.VncClient.Avalonia</c> package (which ships a
/// working <c>VncView</c>/<c>RfbRenderTarget</c> Avalonia control that does this exact job) — using
/// that would mean <c>PolyTerm.Protocols.Vnc</c> takes an Avalonia dependency and <c>IFramebufferChannel</c>
/// stays a dead, unused abstraction for VNC specifically. Confirmed via the library's own real source
/// (<c>RfbRenderTarget.GrabFramebufferReference</c>/<c>AvaloniaFramebufferReference</c> on GitHub) that
/// there's no per-region dirty-rect tracking anywhere in the library either — every update, however
/// small, triggers a full-buffer repaint via <c>InvalidateVisual()</c>. This mirrors that exact
/// behavior: every <see cref="IFramebufferReference"/> grab+dispose cycle raises <see cref="RegionUpdated"/>
/// for the whole current framebuffer, not a computed sub-rectangle.
/// </summary>
internal sealed class VncFramebufferChannel : IFramebufferChannel, IRenderTarget, IDisposable
{
    /// <summary>
    /// R at bit-shift 0, G at 8, B at 16, A at 24 — in little-endian memory this lays out as bytes
    /// [R,G,B,A], matching <see cref="FramebufferFormat.Rgba32"/>'s contract and Avalonia's
    /// <c>PixelFormat.Rgba8888</c> memory-byte-order convention that <c>FramebufferSurface</c> blits
    /// against. Deliberately NOT <see cref="PixelFormat.Plain"/> (R:24,G:16,B:8,A:0): confirmed via a
    /// real end-to-end test against a fake RFB server (<c>VncPixelFormatTests</c>) that Plain's
    /// bit-shift convention produces bytes in [A,B,G,R] order once written to little-endian memory —
    /// a genuine, confirmed color-scrambling bug this custom format fixes, not just a naming choice.
    /// </summary>
    private static readonly PixelFormat TargetFormat =
        new("PolyTermRgba32", bitsPerPixel: 32, depth: 32, bigEndian: false, trueColor: true, hasAlpha: true,
            redMax: 255, greenMax: 255, blueMax: 255, alphaMax: 255,
            redShift: 0, greenShift: 8, blueShift: 16, alphaShift: 24);

    private readonly object _bufferLock = new();
    private byte[]? _buffer;
    private GCHandle _handle;
    private int _width;
    private int _height;

    /// <summary>Set by <see cref="VncSession"/> once <c>VncClient.ConnectAsync</c> returns — null until then.</summary>
    public RfbConnection? Connection { get; set; }

    /// <summary>Tracks all currently-held mouse buttons — the RFB protocol's PointerEvent message always carries the full current button state, not just what changed.</summary>
    private MouseButtons _heldButtons = MouseButtons.None;

    public FramebufferFormat Format => FramebufferFormat.Rgba32;
    public int Width  { get { lock (_bufferLock) return _width; } }
    public int Height { get { lock (_bufferLock) return _height; } }

    public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;

    /// <summary>Never fires — VNC clipboard sync isn't wired up in this first rung (see docs/todos.md), matching RDP's own documented scope trim.</summary>
#pragma warning disable CS0067 // required by IFramebufferChannel; intentionally never raised
    public event EventHandler<ClipboardPayload>? ClipboardReceived;
#pragma warning restore CS0067

    // ── IRenderTarget (called by MarcusW.VncClient on its own background thread) ──────────────────

    public IFramebufferReference GrabFramebufferReference(Size size, IImmutableSet<Screen> layout)
    {
        lock (_bufferLock)
        {
            if (_buffer is null || _width != size.Width || _height != size.Height)
            {
                if (_handle.IsAllocated) _handle.Free();
                _width  = size.Width;
                _height = size.Height;
                _buffer = new byte[Math.Max(1, _width) * Math.Max(1, _height) * 4]; // RGBA32, tightly packed
                _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            }

            return new VncFramebufferReference(_handle.AddrOfPinnedObject(), size, TargetFormat, OnFramebufferReleased);
        }
    }

    private void OnFramebufferReleased()
    {
        byte[] snapshot;
        int w, h;
        lock (_bufferLock)
        {
            if (_buffer is null) return;
            w = _width;
            h = _height;
            snapshot = (byte[])_buffer.Clone(); // copy out — the pinned buffer gets reused/overwritten by the next grab
        }
        RegionUpdated?.Invoke(this, new FramebufferRegionUpdatedEventArgs { X = 0, Y = 0, Width = w, Height = h, Data = snapshot });
    }

    // ── IFramebufferChannel (called by the App layer) ─────────────────────────────────────────────

    public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default)
    {
        var connection = Connection;
        if (connection is null) return ValueTask.CompletedTask;

        var button = e.Button switch
        {
            PointerButton.Left        => MouseButtons.Left,
            PointerButton.Middle      => MouseButtons.Middle,
            PointerButton.Right       => MouseButtons.Right,
            PointerButton.WheelUp     => MouseButtons.WheelUp,
            PointerButton.WheelDown   => MouseButtons.WheelDown,
            _                         => MouseButtons.None,
        };

        _heldButtons = e.Kind switch
        {
            PointerEventKind.Press   => _heldButtons | button,
            PointerEventKind.Release => _heldButtons & ~button,
            _                        => _heldButtons, // Move: carry whatever's already held
        };

        connection.EnqueueMessage(new PointerEventMessage(new Position(e.X, e.Y), _heldButtons), ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default)
    {
        var connection = Connection;
        if (connection is null || !Enum.TryParse<KeySymbol>(e.KeySymbol, out var symbol))
            return ValueTask.CompletedTask;

        connection.EnqueueMessage(new KeyEventMessage(e.Kind == KeyEventKind.Press, symbol), ct);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// RFB has no dedicated "send Ctrl+Alt+Del" message — real VNC clients' equivalent button just
    /// sends the same three keys a physical keypress would generate, in order: press Ctrl, press Alt,
    /// press Delete, then release in reverse. Unlike RDP (see <c>RdpNativeHostChannel</c>'s own
    /// implementation), nothing OS-level intercepts this at the protocol level, so plain key events
    /// genuinely are the correct mechanism here, not a workaround.
    /// </summary>
    public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default)
    {
        var connection = Connection;
        if (connection is null) return ValueTask.CompletedTask;

        connection.EnqueueMessage(new KeyEventMessage(true, KeySymbol.Control_L), ct);
        connection.EnqueueMessage(new KeyEventMessage(true, KeySymbol.Alt_L), ct);
        connection.EnqueueMessage(new KeyEventMessage(true, KeySymbol.Delete), ct);
        connection.EnqueueMessage(new KeyEventMessage(false, KeySymbol.Delete), ct);
        connection.EnqueueMessage(new KeyEventMessage(false, KeySymbol.Alt_L), ct);
        connection.EnqueueMessage(new KeyEventMessage(false, KeySymbol.Control_L), ct);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Best-effort: sends <c>SetDesktopSizeMessage</c>, which not every VNC server honors (it's an
    /// optional RFB extension) — swallows failures the same way RDP's own live-resize does, a server
    /// that doesn't support it just keeps its current resolution, no worse than before. Only handles
    /// the single-screen case (resizes that one screen to the new size); a multi-monitor VNC layout
    /// is left unchanged rather than guessed at — out of scope for a first rung.
    /// </summary>
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        var connection = Connection;
        if (connection is null) return ValueTask.CompletedTask;
        try
        {
            var newSize = new Size(width, height);
            var message = new SetDesktopSizeMessage((currentSize, currentLayout) =>
            {
                if (currentLayout.Count != 1) return (currentSize, currentLayout);
                var screen = currentLayout.Single();
                var resized = new Screen(screen.Id, new Rectangle(Position.Origin, newSize), screen.Flags);
                return (newSize, ImmutableHashSet.Create(resized));
            });
            connection.EnqueueMessage(message, ct);
        }
        catch (Exception)
        {
            // Not supported by this server, or no screen layout known yet — ignore.
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>No-op — see <see cref="ClipboardReceived"/>.</summary>
    public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>Clones the current pinned framebuffer — a defensive copy is required since the pinned buffer is reused/overwritten by the next incoming update.</summary>
    public byte[]? CaptureSnapshot()
    {
        lock (_bufferLock) return _buffer is null ? null : (byte[])_buffer.Clone();
    }

    public void Dispose()
    {
        lock (_bufferLock)
        {
            if (_handle.IsAllocated) _handle.Free();
            _buffer = null;
        }
    }

    private sealed class VncFramebufferReference : IFramebufferReference
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public IntPtr Address { get; }
        public Size Size { get; }
        public PixelFormat Format { get; }
        public double HorizontalDpi => 96.0;
        public double VerticalDpi => 96.0;

        public VncFramebufferReference(IntPtr address, Size size, PixelFormat format, Action onDispose)
        {
            Address    = address;
            Size       = size;
            Format     = format;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
