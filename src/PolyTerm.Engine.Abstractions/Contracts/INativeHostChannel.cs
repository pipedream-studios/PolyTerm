namespace PolyTerm.Engine;

/// <summary>
/// Handle to a native window/control owned and rendered by the protocol module itself, for the App
/// to reparent into a <see cref="SurfaceKind.NativeHost"/> surface. Currently unimplemented by any
/// protocol module (RDP used this via the Windows MSTSC ActiveX control before moving to FreeRDP/
/// <see cref="IFramebufferChannel"/> for cross-platform support) — kept for a future protocol that
/// wants to embed a first-party native control rather than reimplement its rendering.
/// Unlike <see cref="IFramebufferChannel"/>, the protocol module renders pixels and handles input
/// directly — the App only positions and sizes the native window. <see cref="CaptureSnapshot"/>/
/// <see cref="SendPointerAsync"/>/<see cref="SendKeyAsync"/> exist so callers that don't care which
/// kind of graphical session they have (an MCP screenshot/click/type/key tool, say) can treat this
/// the same way as <see cref="IFramebufferChannel"/> despite the very different rendering model
/// underneath — OS-level window capture/input-injection here, protocol-level pixels/events there.
/// </summary>
public interface INativeHostChannel
{
    /// <summary>The child window handle to reparent, or 0 before the underlying control has created one.</summary>
    nint Handle { get; }

    /// <summary>Fires once <see cref="Handle"/> becomes valid (non-zero), typically just after connect.</summary>
    event EventHandler? HandleChanged;

    /// <summary>Requests the native control resize to the given dimensions.</summary>
    ValueTask ResizeAsync(int width, int height, CancellationToken ct = default);

    /// <summary>
    /// Whether the most recent <see cref="ResizeAsync"/> call actually renegotiated the remote
    /// resolution (true), fell back to a client-side scale-to-fit because the server doesn't support
    /// live resize (false), or no resize has been attempted yet (null). Implementations that don't
    /// distinguish the two (e.g. a protocol whose resize is always effective) may always report null.
    /// </summary>
    bool? SupportsLiveResize { get; }

    /// <summary>Fires when <see cref="SupportsLiveResize"/> changes to a new, non-equal value.</summary>
    event EventHandler? SupportsLiveResizeChanged;

    /// <summary>Captures the native window's current contents as top-down, row-major BGRA32 pixel bytes, or null if it doesn't have a valid <see cref="Handle"/> yet or the capture failed.</summary>
    (byte[] Data, int Width, int Height)? CaptureSnapshot();

    /// <summary>Sends a mouse move/press/release event to the native window.</summary>
    ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default);

    /// <summary>Sends a key press/release event to the native window. <see cref="KeyEvent.KeySymbol"/> is either a single literal character (typed as Unicode text) or a named key — see the implementation for the supported name set.</summary>
    ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default);

    /// <summary>
    /// Sends the Ctrl+Alt+Del key combination (Secure Attention Sequence) to the remote session.
    /// Physical Ctrl+Alt+Del can never reach this (or any) app — the local OS intercepts it before any
    /// application sees it — and for RDP specifically, synthesizing it via <see cref="SendKeyAsync"/>
    /// (Win32 <c>PostMessage</c>-based key injection) wouldn't reliably work even if it could be
    /// triggered some other way: Windows' Secure Attention Sequence is deliberately not deliverable
    /// through normal synthetic input. This is a dedicated action instead, matching how every real RDP
    /// client provides one.
    /// </summary>
    ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default);
}
