using System.Runtime.InteropServices;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Bridges the native shim's callback-based framebuffer/state model to <see cref="IFramebufferChannel"/>
/// — the same role <c>VncFramebufferChannel</c> plays for VNC (issue #67). Unlike VNC's underlying
/// library (<c>MarcusW.VncClient</c>, which really has no dirty-rect tracking at all - see
/// <c>VncFramebufferChannel</c>'s own comment), FreeRDP's own GDI layer tracks a real per-frame
/// invalid region (<c>update-&gt;BeginPaint</c>/<c>EndPaint</c>, exposed by the shim as just the
/// changed sub-rectangle) - <see cref="RegionUpdated"/> reflects that real sub-rectangle instead of
/// re-sending the whole frame on every change, which is what made RDP sessions feel laggy even for a
/// single moved/redrawn button (confirmed the difference directly: a full 1024x768 frame is ~3MB
/// copied+allocated+blitted per update; a button repaint is usually a few KB).
/// </summary>
internal sealed class RdpFreerdpFramebufferChannel : IFramebufferChannel, IDisposable
{
    // PTR_FLAGS_*/KBD_FLAGS_* from FreeRDP's own input.h.
    private const ushort PtrFlagsMove = 0x0800;
    private const ushort PtrFlagsDown = 0x8000;
    private const ushort PtrFlagsButton1 = 0x1000;
    private const ushort PtrFlagsButton2 = 0x2000;
    private const ushort PtrFlagsButton3 = 0x4000;
    private const ushort PtrFlagsWheel = 0x0200;
    private const ushort PtrFlagsWheelNegative = 0x0100;

    private readonly object _lock = new();
    private readonly NativeMethods.FramebufferCallback _onFramebuffer;
    private readonly NativeMethods.StateCallback _onState;
    private nint _handle;
    private int _width;
    private int _height;
    /// <summary>
    /// The full desktop image, patched incrementally as each dirty-rect <see cref="RegionUpdated"/>
    /// arrives, so <see cref="CaptureSnapshot"/> can still return a complete picture at any point -
    /// only the small changed region itself gets sent as the event payload.
    /// </summary>
    private byte[]? _fullFrame;
    private TaskCompletionSource<bool>? _connectTcs;
    private string? _connectError;

    public FramebufferFormat Format => FramebufferFormat.Rgba32;
    public int Width { get { lock (_lock) return _width; } }
    public int Height { get { lock (_lock) return _height; } }

    public event EventHandler<FramebufferRegionUpdatedEventArgs>? RegionUpdated;

    /// <summary>Never fires — clipboard sync isn't wired up in this first rung, matching the documented feature-ladder scope trim (docs/polyterm-spec.md §5.2) and VNC's own equivalent gap.</summary>
#pragma warning disable CS0067
    public event EventHandler<ClipboardPayload>? ClipboardReceived;
#pragma warning restore CS0067

    public RdpFreerdpFramebufferChannel()
    {
        // Kept as fields so these delegates - and the native thunks the CLR creates for them - stay
        // alive for the channel's whole lifetime; letting them get GC'd while the shim might still
        // call back would corrupt the process.
        _onFramebuffer = OnFramebuffer;
        _onState = OnState;
    }

    /// <summary>Connects via the native shim and waits for the first state callback (success or failure) before returning.</summary>
    public async Task ConnectAsync(string host, int port, string username, string password, string? domain,
                                   int width, int height, CancellationToken ct)
    {
        _width = width;
        _height = height;
        _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = NativeMethods.prdp_connect(host, port, username, password, domain ?? "", width, height,
            _onFramebuffer, _onState, 0);
        if (handle == 0)
            throw new InvalidOperationException("The native RDP shim rejected the connection arguments.");
        _handle = handle;

        await using var reg = ct.Register(() => _connectTcs.TrySetCanceled(ct));
        var connected = await _connectTcs.Task.ConfigureAwait(false);
        if (!connected)
            throw new InvalidOperationException(_connectError ?? "RDP connection failed.");
    }

    private void OnFramebuffer(nint userData, nint buffer, int fullWidth, int fullHeight,
        int regionX, int regionY, int regionWidth, int regionHeight, int stride)
    {
        if (fullWidth <= 0 || fullHeight <= 0 || regionWidth <= 0 || regionHeight <= 0) return;

        // Tightly-packed copy of just the changed region - this is the event payload, deliberately
        // small (RegionUpdated consumers like FramebufferSurface blit it at [regionX, regionY], not
        // assume it's the whole frame).
        var regionRowBytes = regionWidth * 4;
        var region = new byte[regionRowBytes * regionHeight];
        for (var row = 0; row < regionHeight; row++)
            Marshal.Copy(IntPtr.Add(buffer, row * stride), region, row * regionRowBytes, regionRowBytes);

        lock (_lock)
        {
            if (_fullFrame is null || _width != fullWidth || _height != fullHeight)
                _fullFrame = new byte[fullWidth * fullHeight * 4];
            _width = fullWidth;
            _height = fullHeight;

            // Patch the persisted full-frame copy so CaptureSnapshot() (the MCP screenshot tool)
            // keeps returning a complete image, not just whatever last changed.
            var fullRowBytes = fullWidth * 4;
            for (var row = 0; row < regionHeight; row++)
            {
                var destOffset = (regionY + row) * fullRowBytes + regionX * 4;
                Array.Copy(region, row * regionRowBytes, _fullFrame, destOffset, regionRowBytes);
            }
        }

        RegionUpdated?.Invoke(this, new FramebufferRegionUpdatedEventArgs
        {
            X = regionX, Y = regionY, Width = regionWidth, Height = regionHeight, Data = region,
        });
    }

    private void OnState(nint userData, int state, nint message)
    {
        if (state == 1)
        {
            _connectTcs?.TrySetResult(true);
            return;
        }

        var text = message != 0 ? Marshal.PtrToStringAnsi(message) : null;
        if (_connectTcs is { Task.IsCompleted: false })
        {
            _connectError = text;
            _connectTcs.TrySetResult(false);
        }
        // else: a clean/async disconnect after an already-successful connect - nothing further to do
        // here; RdpFreerdpSession owns reflecting that into SessionState.
    }

    public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default)
    {
        if (_handle == 0) return ValueTask.CompletedTask;

        ushort flags;
        if (e.Button is PointerButton.WheelUp or PointerButton.WheelDown)
        {
            flags = (ushort)(PtrFlagsWheel | (e.Button == PointerButton.WheelDown ? PtrFlagsWheelNegative : 0) | 0x0078);
        }
        else
        {
            var buttonFlag = e.Button switch
            {
                PointerButton.Left => PtrFlagsButton1,
                PointerButton.Right => PtrFlagsButton2,
                PointerButton.Middle => PtrFlagsButton3,
                _ => (ushort)0,
            };
            flags = e.Kind switch
            {
                PointerEventKind.Move => PtrFlagsMove,
                PointerEventKind.Press => (ushort)(buttonFlag | PtrFlagsDown),
                PointerEventKind.Release => buttonFlag,
                _ => (ushort)0,
            };
        }

        NativeMethods.prdp_send_mouse(_handle, flags, e.X, e.Y);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default)
    {
        if (_handle == 0) return ValueTask.CompletedTask;
        var down = e.Kind == KeyEventKind.Press ? 1 : 0;

        // Printable characters arrive as a numeric X11 keysym string (FramebufferSurface.OnTextInput's
        // CharToKeySymbol: the code point directly for Latin-1, 0x01000000 + code point beyond it -
        // same convention VncFramebufferChannel consumes via MarcusW.VncClient's own KeySymbol enum).
        // Routed through the shim's Unicode keyboard-input entry point rather than a scancode: RDP
        // scancodes are physical-key-based (sending 'a' and 'A' would need a synthesized Shift
        // press/release around the same key, which this single press-or-release event has no state to
        // drive), while Unicode input already carries the exact resulting character and needs no
        // shift/layout handling at all - the correct mechanism for typed text, not a workaround.
        if (int.TryParse(e.KeySymbol, out var keysym))
        {
            var codePoint = keysym <= 0xFF ? keysym : keysym - 0x01000000;
            if (codePoint is < 0 or > 0xFFFF) return ValueTask.CompletedTask; // outside the BMP - not representable as the single UTF-16 code unit prdp_send_unicode_key takes
            NativeMethods.prdp_send_unicode_key(_handle, down, (ushort)codePoint);
            return ValueTask.CompletedTask;
        }

        // Otherwise a named control key (FramebufferSurface.ControlKeyMap's output - Return,
        // arrows, F-keys, modifiers, ...), which genuinely is a physical key with no associated
        // character, so a real scancode is correct here.
        if (!RdpScancodes.TryMap(e.KeySymbol, out var scancode))
            return ValueTask.CompletedTask;

        NativeMethods.prdp_send_key(_handle, down, scancode);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Unlike RDP's own MSTSC ActiveX path (which has native SAS support via <c>AdvancedSettings2.SasSequence</c>),
    /// the FreeRDP client API this shim binds has no equivalent "send the real SAS" primitive exposed at
    /// this layer - matching VNC's own approach (see <c>VncFramebufferChannel</c>), this synthesizes
    /// Ctrl+Alt+Del as plain key events instead. Revisit if FreeRDP's client.h grows a real SAS API.
    /// </summary>
    public ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default)
    {
        if (_handle == 0) return ValueTask.CompletedTask;
        // Via RdpScancodes rather than duplicated magic numbers - Delete is an "extended" scancode
        // (0x53 | 0x100), and hardcoding just 0x53 here previously sent the numpad-period scancode
        // instead of the real Delete key.
        RdpScancodes.TryMap("Control_L", out var ctrl);
        RdpScancodes.TryMap("Alt_L", out var alt);
        RdpScancodes.TryMap("Delete", out var del);
        NativeMethods.prdp_send_key(_handle, 1, ctrl);
        NativeMethods.prdp_send_key(_handle, 1, alt);
        NativeMethods.prdp_send_key(_handle, 1, del);
        NativeMethods.prdp_send_key(_handle, 0, del);
        NativeMethods.prdp_send_key(_handle, 0, alt);
        NativeMethods.prdp_send_key(_handle, 0, ctrl);
        return ValueTask.CompletedTask;
    }

    /// <summary>Not implemented in this first rung (feature-ladder: "connect/display/input" only) — the shim has no resize entry point yet.</summary>
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>No-op — see <see cref="ClipboardReceived"/>.</summary>
    public ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default) => ValueTask.CompletedTask;

    public byte[]? CaptureSnapshot()
    {
        lock (_lock) return _fullFrame is null ? null : (byte[])_fullFrame.Clone();
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.prdp_disconnect(_handle);
            NativeMethods.prdp_free(_handle);
            _handle = 0;
        }
    }
}
