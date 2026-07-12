namespace PolyTerm.Engine;

/// <summary>
/// Handle to an embedded browser session bound to a <see cref="SurfaceKind.WebView"/> surface. Unlike
/// every other capability channel, this one has no independent existence until the App actually
/// builds the surface control for its tab — the "connection" is the native browser control itself,
/// which doesn't exist yet when <see cref="IProtocolSession.ConnectAsync"/> returns. <see cref="AttachHost"/>
/// is how the not-yet-existent surface registers itself once it's actually built.
/// </summary>
public interface IWebChannel
{
    /// <summary>The URL this channel currently reflects — the profile's configured URL until a host attaches and reports a real navigation.</summary>
    Uri CurrentUri { get; }

    /// <summary>Fires whenever <see cref="CurrentUri"/> changes, including reports from an attached host.</summary>
    event EventHandler<Uri>? Navigated;

    /// <summary>Navigates to the given URL. Forwarded to the attached host if one is present.</summary>
    Task NavigateAsync(Uri uri, CancellationToken ct = default);

    /// <summary>
    /// Registers (or clears, with null) the surface currently rendering this session, called by
    /// the surface control itself as it attaches to/detaches from the visual tree.
    /// </summary>
    void AttachHost(IWebViewHost? host);

    /// <summary>
    /// Reports the real, post-navigation URL the attached host just settled on (e.g. after a
    /// redirect), updating <see cref="CurrentUri"/> to match and raising <see cref="Navigated"/>.
    /// Called only by the attached host itself — no other caller should ever push this.
    /// </summary>
    void ReportNavigated(Uri uri);

    /// <summary>Executes JavaScript in the page's top-level document, or null if no host is attached yet.</summary>
    Task<string?> InvokeScriptAsync(string script, CancellationToken ct = default);

    /// <summary>Captures the page as an already-PNG-encoded image, or null if no host is attached yet or the capture isn't supported on this platform.</summary>
    Task<byte[]?> CaptureScreenshotAsync(CancellationToken ct = default);
}

/// <summary>
/// Implemented by the App's WebView surface control. Registered onto an <see cref="IWebChannel"/>
/// via <see cref="IWebChannel.AttachHost"/> once the tab actually renders, so protocol-level code
/// can drive the real browser control without <c>PolyTerm.Protocols.Web</c> taking any Avalonia
/// dependency of its own.
/// </summary>
public interface IWebViewHost
{
    /// <summary>Navigates the real, rendered browser control to the given URL.</summary>
    Task NavigateAsync(Uri uri, CancellationToken ct);

    /// <summary>Executes JavaScript in the real control's top-level document.</summary>
    Task<string?> InvokeScriptAsync(string script, CancellationToken ct);

    /// <summary>
    /// Captures the real control's contents as an already-PNG-encoded image, or null where
    /// unsupported (see the implementing surface for which platforms support this).
    /// </summary>
    Task<byte[]?> CaptureScreenshotAsync(CancellationToken ct);
}
