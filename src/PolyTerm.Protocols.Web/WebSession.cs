using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>
/// An HTTP/S "session": launches the profile's URL in the OS default browser and immediately reports
/// itself Connected — there is nothing to hold open. All capability accessors are null
/// (<see cref="SurfaceKind.External"/> means the browser window itself is the surface; the App renders
/// no tab for this session — see <c>MainViewModel.OnSessionOpened</c>).
/// </summary>
public sealed class WebSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly string _scheme;
    private readonly ILogger<WebSession> _logger;
    private SessionState _state = SessionState.Created;

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
            _logger.LogInformation("Opening {Url} in the default browser.", url);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetState(SessionState.Connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch browser for {Host}.", _ctx.Profile.Host);
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
        return Task.CompletedTask;
    }

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
