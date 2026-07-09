using System.Runtime.InteropServices;
using AxMSTSCLib;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Rdp;

/// <summary>
/// Exposes the MSTSC ActiveX control's window handle to the App for reparenting into a
/// <see cref="SurfaceKind.NativeHost"/> surface. <see cref="Handle"/> stays 0 (and
/// <see cref="HandleChanged"/> doesn't fire) until <see cref="RdpSession"/> calls
/// <see cref="MarkConnected"/> from its <c>OnConnected</c> handler, so the App never reparents a
/// not-yet-connected control.
/// <see cref="CaptureSnapshot"/>/<see cref="SendPointerAsync"/>/<see cref="SendKeyAsync"/> (added for
/// the MCP framebuffer tools — see <c>McpBridgeService</c>) delegate to <see cref="Win32NativeWindowInput"/>
/// — real Win32 window capture and synthetic-input APIs against <see cref="Handle"/>, since the
/// ActiveX control itself exposes no scripting surface for either. That helper is verified against a
/// real (throwaway, non-RDP) window handle in <c>PolyTerm.App.Tests</c>; neither it nor this class has
/// been click-tested against a real RDP session in this environment (see docs/todos.md).
/// </summary>
internal sealed class RdpNativeHostChannel : INativeHostChannel
{
    private readonly AxMsRdpClient11NotSafeForScripting _control;

    public nint Handle { get; private set; }
    public bool? SupportsLiveResize { get; private set; }

    public event EventHandler? HandleChanged;
    public event EventHandler? SupportsLiveResizeChanged;

    public RdpNativeHostChannel(AxMsRdpClient11NotSafeForScripting control) => _control = control;

    /// <summary>Called once the RDP connection is established; publishes the control's (already-created) window handle.</summary>
    public void MarkConnected()
    {
        Handle = _control.Handle;
        HandleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Best-effort live resolution renegotiation via <c>UpdateSessionDisplaySettings</c> (RDP 8.1+
    /// Dynamic Resolution Update — resizes without disconnecting). Deliberately <b>not</b>
    /// <c>Reconnect(width, height)</c>: confirmed via repro against a real host that despite its
    /// name, calling it on an already-connected session tears the connection down (fires
    /// <c>OnDisconnected</c>) rather than resizing in place — unacceptable for something triggered
    /// on every window resize. <c>UpdateSessionDisplaySettings</c> isn't universally supported either
    /// (confirmed via the same repro: throws <c>E_UNEXPECTED</c> on a server that lacks RDP 8.1+
    /// Dynamic Resolution Update — this is common for anything other than a fairly recent Windows
    /// Server), but critically it fails *without* disconnecting, so swallowing the failure here is
    /// safe: on unsupported servers this is a silent no-op and <c>SmartSizing</c>'s
    /// aspect-preserving scale-to-fit is what the user sees, same as if this were never called.
    /// </summary>
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        var w = (uint)Math.Max(200, width);
        var h = (uint)Math.Max(200, height);
        bool supported;
        try
        {
            _control.UpdateSessionDisplaySettings(w, h, w, h, 0, 100, 100);
            supported = true;
        }
        catch (COMException)
        {
            // Not supported by this server — SmartSizing's letterboxed scale-to-fit remains in effect.
            supported = false;
        }

        if (SupportsLiveResize != supported)
        {
            SupportsLiveResize = supported;
            SupportsLiveResizeChanged?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }

    public (byte[] Data, int Width, int Height)? CaptureSnapshot() => Win32NativeWindowInput.CaptureWindow(Handle);

    public ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default)
    {
        Win32NativeWindowInput.SendPointer(Handle, e.X, e.Y, e.Button, e.Kind);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default)
    {
        Win32NativeWindowInput.SendKey(Handle, e.KeySymbol, e.Kind);
        return ValueTask.CompletedTask;
    }
}
