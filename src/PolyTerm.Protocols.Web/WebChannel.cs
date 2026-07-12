using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>
/// Default <see cref="IWebChannel"/> implementation. Thin and mostly passive by design — see this
/// module's own <see cref="WebSession"/> doc comment for why: the real browser control doesn't exist
/// until the App builds a <c>WebSurface</c> for this session's tab, well after <see cref="WebSession.ConnectAsync"/>
/// has already returned, so there's nothing for this channel to actively drive until a host attaches.
/// </summary>
public sealed class WebChannel : IWebChannel
{
    private IWebViewHost? _host;

    public Uri CurrentUri { get; private set; }

    public event EventHandler<Uri>? Navigated;

    public WebChannel(Uri initialUri) => CurrentUri = initialUri;

    public async Task NavigateAsync(Uri uri, CancellationToken ct = default)
    {
        CurrentUri = uri;
        Navigated?.Invoke(this, uri);
        if (_host is not null)
            await _host.NavigateAsync(uri, ct);
    }

    public void AttachHost(IWebViewHost? host) => _host = host;

    public void ReportNavigated(Uri uri)
    {
        CurrentUri = uri;
        Navigated?.Invoke(this, uri);
    }

    public Task<string?> InvokeScriptAsync(string script, CancellationToken ct = default) =>
        _host?.InvokeScriptAsync(script, ct) ?? Task.FromResult<string?>(null);

    public Task<byte[]?> CaptureScreenshotAsync(CancellationToken ct = default) =>
        _host?.CaptureScreenshotAsync(ct) ?? Task.FromResult<byte[]?>(null);
}
