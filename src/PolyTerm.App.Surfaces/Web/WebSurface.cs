using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using PolyTerm.Engine;

namespace PolyTerm.App.Surfaces.Web;

/// <summary>
/// Hosts an embedded browser tab bound to an <see cref="IWebChannel"/>, wrapping Avalonia's
/// <see cref="NativeWebView"/> (not subclassing it — unlike <see cref="Rdp.RdpHostSurface"/>'s
/// <see cref="NativeControlHost"/> base, <see cref="NativeWebView"/> isn't designed to be
/// subclassed). Registers itself onto the channel via <see cref="IWebChannel.AttachHost"/> once
/// actually attached to the visual tree — see <see cref="IWebChannel"/>'s own doc comment for why
/// that handshake exists.
/// </summary>
public sealed class WebSurface : Decorator, IWebViewHost
{
    public static readonly StyledProperty<IWebChannel?> ChannelProperty =
        AvaloniaProperty.Register<WebSurface, IWebChannel?>(nameof(Channel));

    public IWebChannel? Channel
    {
        get => GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    /// <summary>The page's document title, refreshed after each navigation — bound one-way-to-source onto <c>SessionTabViewModel.OscTitle</c>, matching the terminal surface's OSC-title convention.</summary>
    public static readonly StyledProperty<string> PageTitleProperty =
        AvaloniaProperty.Register<WebSurface, string>(nameof(PageTitle), defaultValue: "");

    public string PageTitle
    {
        get => GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }

    private readonly NativeWebView _webView;
    private IWebChannel? _attachedChannel;

    public WebSurface()
    {
        _webView = new NativeWebView();
        _webView.NavigationCompleted += OnNavigationCompleted;
        Child = _webView;
        AutomationProperties.SetAutomationId(this, "WebSurface");
    }

    // Decorator's own default automation peer doesn't surface this control (or its AutomationId) in
    // the UIA tree at all - confirmed live via FlaUI: without this override, "WebSurface" was
    // unreachable by AutomationId even though the NativeWebView child rendered and navigated
    // correctly. A plain ControlAutomationPeer is enough to make the AutomationId set below queryable.
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reattach(Channel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChannelProperty)
            Reattach(change.NewValue as IWebChannel);
    }

    private void Reattach(IWebChannel? channel)
    {
        Detach();
        _attachedChannel = channel;
        if (channel is null)
            return;

        channel.AttachHost(this);
        _webView.Source = channel.CurrentUri;
    }

    private void Detach()
    {
        if (_attachedChannel is null)
            return;
        _attachedChannel.AttachHost(null);
        _attachedChannel = null;
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        _attachedChannel?.ReportNavigated(_webView.Source);
        // async void: a failing title read shouldn't crash the app, just leave PageTitle stale.
        try { PageTitle = await _webView.InvokeScript("document.title") ?? ""; }
        catch { /* best-effort title only */ }
    }

    // ─── IWebViewHost — lets PolyTerm.Protocols.Web drive the real control without an Avalonia dependency ───

    Task IWebViewHost.NavigateAsync(Uri uri, CancellationToken ct)
    {
        _webView.Navigate(uri);
        return Task.CompletedTask;
    }

    Task<string?> IWebViewHost.InvokeScriptAsync(string script, CancellationToken ct) =>
        _webView.InvokeScript(script);

    /// <summary>
    /// Not implemented in this pass — always returns null (the documented "unsupported" outcome).
    /// <see cref="NativeWebView.TryGetPlatformHandle"/> exposes a real capture path on Windows
    /// (<c>IWindowsWebView2PlatformHandle.CoreWebView2</c> → <c>ICoreWebView2.CapturePreviewAsync</c>),
    /// but that interface is a raw COM vtable — calling it correctly means declaring every preceding
    /// <c>ICoreWebView2</c> member in exact ABI order plus a callback COM interface for the
    /// completion handler, none of which can be verified without a live WebView2 instance to test
    /// against. Left as a follow-up rather than shipped as unverified interop code.
    /// </summary>
    Task<byte[]?> IWebViewHost.CaptureScreenshotAsync(CancellationToken ct) =>
        Task.FromResult<byte[]?>(null);
}
