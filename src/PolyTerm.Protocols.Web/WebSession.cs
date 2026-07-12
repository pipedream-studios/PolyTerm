using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>
/// An HTTP/S session. By default, embeds the profile's URL in a <see cref="SurfaceKind.WebView"/> tab
/// via <see cref="Web"/> — nothing to hold open beyond that channel, since the real browser control is
/// owned and driven by the App's WebView surface once it attaches (see <see cref="WebChannel"/>'s own
/// doc comment). When the profile's "openExternally" setting is true, falls back to the original
/// behavior instead: launches the OS default browser and exposes no capabilities at all
/// (<see cref="SurfaceKind.External"/> — the browser window itself is the surface, and the App renders
/// no tab for this session; see <c>MainViewModel.OnSessionOpened</c>).
/// </summary>
public sealed class WebSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly string _scheme;
    private readonly ILogger<WebSession> _logger;
    private SessionState _state = SessionState.Created;
    private WebChannel? _channel;

    public Guid SessionId => _ctx.SessionId;
    public ConnectionProfile Profile => _ctx.Profile;
    public SessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    public ITerminalChannel? Terminal => null;
    public IFileTransferChannel? FileTransfer => null;
    public IFramebufferChannel? Framebuffer => null;
    public ITunnelManager? Tunnels => null;
    public INativeHostChannel? NativeHost => null;
    public IWebChannel? Web => _channel;

    public WebSession(SessionContext ctx, string scheme, ILogger<WebSession> logger)
    {
        _ctx    = ctx;
        _scheme = scheme;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            var url = BuildUrl();
            if (GetBoolSetting(_ctx.Profile.Settings, "openExternally"))
            {
                _logger.LogInformation("Opening {Url} in the default browser.", url);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else
            {
                _logger.LogInformation("Embedding {Url} in a WebView tab.", url);
                _channel = new WebChannel(new Uri(url));
            }
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Host}.", _ctx.Profile.Host);
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
        return Task.CompletedTask;
    }

    private static bool GetBoolSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        v.GetBoolean();

    /// <summary>
    /// Uses <see cref="ConnectionProfile.Host"/> as-is if it's already a full URL (so pasting a
    /// complete address with its own scheme/path just works), otherwise builds one from the
    /// descriptor's scheme, the host, and the port (only appended if it differs from the scheme's default).
    /// </summary>
    private string BuildUrl()
    {
        var host = _ctx.Profile.Host ?? throw new InvalidOperationException("No host configured.");
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return host;

        var defaultPort = _scheme == "https" ? 443 : 80;
        var port = _ctx.Profile.Port;
        var portSuffix = port is > 0 && port != defaultPort ? $":{port}" : "";
        return $"{_scheme}://{host}{portSuffix}";
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closed);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            SetState(SessionState.Closed);
        return ValueTask.CompletedTask;
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }
}
