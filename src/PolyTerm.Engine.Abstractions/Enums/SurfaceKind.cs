namespace PolyTerm.Engine;

/// <summary>What kind of rendering surface the App must provide to display a session of a given protocol.</summary>
public enum SurfaceKind
{
    /// <summary>A VT-aware terminal surface bound to <see cref="IProtocolSession.Terminal"/>.</summary>
    Terminal,
    /// <summary>A pixel surface bound to <see cref="IProtocolSession.Framebuffer"/>.</summary>
    Framebuffer,
    /// <summary>An embedded native window/control owned by the protocol module itself.</summary>
    NativeHost,
    /// <summary>Handled outside the App's own windowing, e.g. launched in the system browser.</summary>
    External,
    /// <summary>No visible surface.</summary>
    None,
}
