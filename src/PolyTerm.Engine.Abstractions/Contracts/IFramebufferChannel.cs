namespace PolyTerm.Engine;

/// <summary>Pixel-framebuffer channel for graphical-desktop protocols. Implemented by VNC (<c>VncFramebufferChannel</c>); RDP uses <see cref="INativeHostChannel"/> instead, since its pixels are rendered by a native OS control rather than delivered as protocol-level framebuffer data.</summary>
public interface IFramebufferChannel
{
    /// <summary>Pixel format of the data delivered via <see cref="RegionUpdated"/>.</summary>
    FramebufferFormat Format { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>Fires when a rectangular region of the remote desktop has changed. Raised on an engine thread.</summary>
    event EventHandler<FramebufferRegionUpdatedEventArgs> RegionUpdated;

    /// <summary>Fires when the remote clipboard changes. Raised on an engine thread.</summary>
    event EventHandler<ClipboardPayload> ClipboardReceived;

    /// <summary>Sends a mouse move/press/release event to the remote desktop.</summary>
    ValueTask SendPointerAsync(PointerEvent e, CancellationToken ct = default);

    /// <summary>Sends a key press/release event to the remote desktop.</summary>
    ValueTask SendKeyAsync(KeyEvent e, CancellationToken ct = default);

    /// <summary>
    /// Sends the Ctrl+Alt+Del key combination to the remote desktop. Physical Ctrl+Alt+Del can never
    /// reach this (or any) app — the local OS intercepts it before any application sees it — so this
    /// exists as a dedicated action rather than something reachable via <see cref="SendKeyAsync"/>
    /// alone with modifier keys held down through separate calls, matching how every real VNC/RDP
    /// client provides an explicit "Send Ctrl+Alt+Del" action instead.
    /// </summary>
    ValueTask SendCtrlAltDeleteAsync(CancellationToken ct = default);

    /// <summary>Requests the remote desktop resize to the given dimensions.</summary>
    ValueTask ResizeAsync(int width, int height, CancellationToken ct = default);

    /// <summary>Pushes local clipboard content to the remote session.</summary>
    ValueTask SendClipboardAsync(ClipboardPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Returns the current full framebuffer contents (row-major, in <see cref="Format"/>), or null if
    /// nothing has been received from the server yet. Unlike <see cref="RegionUpdated"/> (a push event
    /// that only fires when something changes), this is a pull — for an idle/static remote desktop
    /// that hasn't changed recently, waiting for the next <see cref="RegionUpdated"/> could mean
    /// waiting indefinitely, so anything needing "the current state, right now" (e.g. an MCP
    /// screenshot tool) should call this instead.
    /// </summary>
    byte[]? CaptureSnapshot();
}
