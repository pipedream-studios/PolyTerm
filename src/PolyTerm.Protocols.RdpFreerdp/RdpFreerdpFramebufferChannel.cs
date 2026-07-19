using System.Runtime.InteropServices;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Bridges the native shim's callback-based framebuffer/state model to <see cref="IFramebufferChannel"/>
/// — the same role <c>VncFramebufferChannel</c> plays for VNC (issue #67). Unlike VNC's RFB (which has
/// no dirty-rect tracking at the library level either), FreeRDP's own <c>update-&gt;EndPaint</c> also
/// fires once per completed frame with the whole backbuffer, so this raises <see cref="RegionUpdated"/>
/// for the whole current framebuffer every time too, not a computed sub-rectangle.
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
    private byte[]? _lastSnapshot;
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

    private void OnFramebuffer(nint userData, nint buffer, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return;

        var rowBytes = width * 4;
        var snapshot = new byte[rowBytes * height];
        if (stride == rowBytes)
        {
            Marshal.Copy(buffer, snapshot, 0, snapshot.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(buffer, y * stride), snapshot, y * rowBytes, rowBytes);
        }

        lock (_lock)
        {
            _width = width;
            _height = height;
            _lastSnapshot = snapshot;
        }
        RegionUpdated?.Invoke(this, new FramebufferRegionUpdatedEventArgs { X = 0, Y = 0, Width = width, Height = height, Data = snapshot });
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
        if (_handle == 0 || !RdpScancodes.TryMap(e.KeySymbol, out var scancode))
            return ValueTask.CompletedTask;

        NativeMethods.prdp_send_key(_handle, e.Kind == KeyEventKind.Press ? 1 : 0, scancode);
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
        const uint ctrl = 0x1D, alt = 0x38, del = 0x53;
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
        lock (_lock) return _lastSnapshot is null ? null : (byte[])_lastSnapshot.Clone();
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
