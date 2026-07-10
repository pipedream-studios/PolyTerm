using AxMSTSCLib;
using Microsoft.Extensions.Logging;
using MSTSCLib;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Rdp;

/// <summary>
/// An RDP connection backed by the OS's MSTSC ActiveX control (<c>AxMsRdpClient11NotSafeForScripting</c>,
/// from <c>mstscax.dll</c>) — the same component <c>mstsc.exe</c>/Remote Desktop Connection uses. The
/// control renders and handles input for its own native window; this session only manages its
/// lifecycle and exposes that window via <see cref="NativeHost"/>. First-rung scope: connect, display,
/// keyboard/mouse input — no resize after connect. Clipboard sync comes free from the control itself.
/// Certificate-trust decisions go through the control's own native warning dialog (see
/// <see cref="ConnectCore"/>'s <c>AuthenticationLevel</c> comment) rather than a PolyTerm-styled prompt
/// — confirmed via reflection over the actual interop assembly that no public event exposes the
/// certificate details or lets managed code supply the accept/reject decision itself.
/// </summary>
public sealed class RdpSession : IProtocolSession
{
    private readonly SessionContext _ctx;
    private readonly ILogger<RdpSession> _logger;

    private AxMsRdpClient11NotSafeForScripting? _control;
    private System.Windows.Forms.Form? _hostForm;
    private RdpNativeHostChannel? _nativeHost;
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
    public INativeHostChannel? NativeHost => _nativeHost;

    public RdpSession(SessionContext ctx, ILogger<RdpSession> logger)
    {
        _ctx    = ctx;
        _logger = logger;
    }

    /// <summary>
    /// Creates and configures the ActiveX control and connects it, on the Avalonia UI thread — it's a
    /// real WinForms/COM control tied to a message pump, so it can't be created or driven from a
    /// background thread. <see cref="NativeHost"/>'s handle becomes available once <c>OnConnected</c> fires.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Connecting);
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ConnectCore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RDP connect failed.");
            SetState(SessionState.Faulted);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs { Exception = ex, IsFatal = true });
            throw;
        }
    }

    private void ConnectCore()
    {
        var profile  = _ctx.Profile;
        var host     = profile.Host ?? throw new InvalidOperationException("No host configured.");
        var port     = profile.Port ?? 3389;
        var username = GetSetting(profile.Settings, "username") ?? _ctx.Credential?.Username ?? Environment.UserName;
        var domain   = GetSetting(profile.Settings, "domain") ?? "";

        var control = new AxMsRdpClient11NotSafeForScripting();

        // No way to know the tab's actual size yet (it doesn't exist until this session finishes
        // connecting) — the primary screen's resolution is a reasonable stand-in, closer to the
        // eventual window's aspect ratio than an arbitrary fixed size.
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new System.Drawing.Rectangle(0, 0, 1280, 800);

        // A parentless AxHost (CreateControl() with no container) never gets a properly-styled
        // native window — its Handle stays 0 indefinitely and the control never fully OLE-activates,
        // confirmed by comparing GetWindowLongPtr(GWL_STYLE) with/without a real container in a
        // standalone repro. Hosting it inside an invisible WinForms Form via Controls.Add gives it a
        // correct WS_CHILD window immediately. The Form is positioned off-screen and never gets its
        // own message loop (Application.Run isn't called) — Avalonia's Win32 backend already pumps
        // this thread's message queue continuously, which services this Form/control's messages too.
        var hostForm = new System.Windows.Forms.Form
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            ShowInTaskbar   = false,
            StartPosition   = System.Windows.Forms.FormStartPosition.Manual,
            Location        = new System.Drawing.Point(-32000, -32000),
            Size            = new System.Drawing.Size(1, 1),
        };
        // Deliberately not Dock = DockStyle.Fill: RdpHostSurface reparents this control's raw HWND
        // out from under hostForm via SetParent (Win32-level only — Control.Parent, WinForms' own
        // bookkeeping, still says hostForm). A docked control re-asserts its size against that
        // logical parent's ClientSize on any WM_SIZE-triggered layout pass, fighting every raw
        // SetWindowPos call RdpHostSurface makes and snapping back to hostForm's 1x1 off-screen size
        // — confirmed live: the desktop rendered, but pinned to a tiny corner of the tab. An explicit
        // Size with no Dock isn't tied to hostForm at all, so nothing overrides RdpHostSurface's resizing.
        control.Size = new System.Drawing.Size(screen.Width, screen.Height);
        hostForm.Controls.Add(control);

        // Forces the control fully up through OLE in-place activation right now. Setting any of the
        // properties below in the window between Controls.Add and full activation throws
        // AxHost.InvalidActiveXStateException — confirmed by hitting it here, then confirming (via a
        // standalone repro) that reading control.Handle first, forcing activation as a documented
        // side effect, avoids it. hostForm.Show() does the same thing via the normal WinForms path.
        hostForm.Show();

        // DesktopWidth/DesktopHeight are required, non-optional — the control has no sane default
        // and renders nothing until they're set. Using the primary screen's resolution (computed
        // above) reduces, but doesn't eliminate, the SmartSizing aspect-preserving letterboxing
        // confirmed on click-test — RdpNativeHostChannel.ResizeAsync renegotiates properly if the
        // server supports RDP 8.1+ Dynamic Resolution Update; on servers that don't (confirmed common
        // via repro), this initial guess is what sticks for the session.
        control.DesktopWidth  = screen.Width;
        control.DesktopHeight = screen.Height;
        control.AdvancedSettings2.SmartSizing = true;

        control.Server   = host;
        control.UserName = username;
        control.Domain   = domain;
        control.AdvancedSettings2.RDPPort = port;
        if (_ctx.Credential?.Secret is { Length: > 0 } secret)
            control.AdvancedSettings2.ClearTextPassword = System.Text.Encoding.UTF8.GetString(secret);

        // AuthenticationLevel controls what the control does when the server's TLS certificate isn't
        // trusted: 0 = connect silently, no warning (the previous, unsafe default here — any cert
        // accepted, no MITM protection); 1 = refuse to connect at all, no way to override; 2 = show the
        // control's own native "certificate problem" dialog and let the user accept or refuse. There is
        // no managed API to intercept the actual certificate or substitute a PolyTerm-styled dialog —
        // confirmed via reflection over the real interop assembly: OnAuthenticationWarningDisplayed/
        // Dismissed (wired below, for logging only) are plain parameterless EventHandlers, and no other
        // public member on this control version exposes the certificate chain or a decision callback.
        // Level 2 is the closest thing to a real accept/reject trust prompt this control offers.
        control.AdvancedSettings7.AuthenticationLevel   = 2;
        control.AdvancedSettings7.EnableCredSspSupport  = true;

        // Drive/audio redirection (M4 "RDP audio/drive rungs"). Confirmed via a live standalone
        // probe against this exact control version, not assumed from docs: RedirectDrives defaults
        // to false and round-trips cleanly; AudioRedirectionMode defaults to 0 and accepts all three
        // documented values (0 = play on this computer, 1 = play on the remote computer, 2 = don't
        // play) without throwing; AudioCaptureRedirectionMode (microphone) defaults to false. There's
        // Per-drive selection (narrowing which specific drives RedirectDrives actually redirects) is
        // applied separately, once connected — see ApplyDriveRedirectionSelection. AdvancedSettings8.RedirectDirectX
        // was also probed and throws NotImplementedException on this control version — left alone,
        // same category as the already-documented "version 13 CLSID" interop gaps.
        control.AdvancedSettings2.RedirectDrives             = GetBoolSetting(profile.Settings, "redirectDrives");
        control.AdvancedSettings6.AudioRedirectionMode       = AudioModeToValue(GetSetting(profile.Settings, "audioMode"));
        control.AdvancedSettings8.AudioCaptureRedirectionMode = GetBoolSetting(profile.Settings, "redirectMicrophone");

        _control    = control;
        _hostForm   = hostForm;
        _nativeHost = new RdpNativeHostChannel(control);

        control.OnConnected                     += OnConnected;
        control.OnDisconnected                   += OnDisconnected;
        control.OnFatalError                     += OnFatalError;
        control.OnWarning                        += OnWarning;
        control.OnAuthenticationWarningDisplayed += OnAuthenticationWarningDisplayed;
        control.OnAuthenticationWarningDismissed += OnAuthenticationWarningDismissed;

        control.Connect();
    }

    // Event-arg shapes (IMsTscAxEvents_OnDisconnectedEvent.discReason etc.) match AxImp's standard
    // codegen for mstscax.dll, stable across Windows versions.
    private void OnConnected(object? sender, EventArgs e)
    {
        _nativeHost?.MarkConnected();
        ApplyDriveRedirectionSelection();
        SetState(SessionState.Connected);
    }

    /// <summary>
    /// Narrows drive redirection to specific drives, the way <c>mstsc.exe</c>'s own "Local Devices and
    /// Resources" dialog does — <c>AdvancedSettings2.RedirectDrives</c> (set in <see cref="ConnectCore"/>)
    /// is otherwise all-or-nothing for every local drive. <c>IMsRdpDriveCollection</c> isn't reachable
    /// through any property this <c>AxHost</c> wrapper itself exposes (confirmed via reflection — it
    /// doesn't implement <see cref="IMsRdpClientNonScriptable7"/> or any earlier tier), but
    /// <c>AxHost.GetOcx()</c> (public, not a workaround) returns the underlying raw COM object, which a
    /// normal C# cast to <see cref="IMsRdpClientNonScriptable7"/> resolves via a real COM
    /// <c>QueryInterface</c> — confirmed via reflection that the real CoClass for this control version
    /// implements that interface. A no-op when no specific drives are configured, so this only changes
    /// behavior for profiles that actually opt into per-drive selection; existing all-or-nothing
    /// <c>RedirectDrives</c> behavior is untouched otherwise.
    /// </summary>
    private void ApplyDriveRedirectionSelection()
    {
        var selected = ParseRedirectedDriveLetters(_ctx.Profile.Settings);
        if (selected.Count == 0 || _control is null) return;

        if (_control.GetOcx() is not IMsRdpClientNonScriptable7 nonScriptable)
        {
            _logger.LogWarning("Could not reach IMsRdpClientNonScriptable7 on the RDP control — per-drive redirection selection skipped.");
            return;
        }

        try
        {
            var drives = nonScriptable.DriveCollection;
            drives.RescanDrives(true);
            for (uint i = 0; i < drives.DriveCount; i++)
            {
                var drive = drives.get_DriveByIndex(i);
                drive.RedirectionState = selected.Contains(drive.Name, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply per-drive redirection selection.");
        }
    }

    private void OnDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        _logger.LogInformation("RDP disconnected (reason {Reason}).", e.discReason);
        SetState(_state == SessionState.Closing ? SessionState.Closed : SessionState.Faulted);
    }

    private void OnFatalError(object? sender, IMsTscAxEvents_OnFatalErrorEvent e)
    {
        _logger.LogError("RDP fatal error {ErrorCode}.", e.errorCode);
        SetState(SessionState.Faulted);
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs
        {
            Exception = new InvalidOperationException($"RDP fatal error {e.errorCode}."),
            IsFatal   = true,
        });
    }

    private void OnWarning(object? sender, IMsTscAxEvents_OnWarningEvent e) =>
        _logger.LogWarning("RDP warning {WarningCode}.", e.warningCode);

    // Fired around the control's own native certificate-warning dialog (see AuthenticationLevel above)
    // — informational only, since nothing here observes or influences the user's actual choice.
    private void OnAuthenticationWarningDisplayed(object? sender, EventArgs e) =>
        _logger.LogWarning("RDP server authentication warning displayed for {Host} — awaiting user response.", _ctx.Profile.Host);

    private void OnAuthenticationWarningDismissed(object? sender, EventArgs e) =>
        _logger.LogInformation("RDP server authentication warning dismissed for {Host}.", _ctx.Profile.Host);

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        SetState(SessionState.Closing);
        if (_control is not null)
        {
            var control = _control;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { control.Disconnect(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting RDP control."); }
            });
        }
        SetState(SessionState.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_state is not (SessionState.Closed or SessionState.Faulted))
            await DisconnectAsync().ConfigureAwait(false);

        if (_hostForm is not null)
        {
            var hostForm = _hostForm;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(hostForm.Dispose);
        }
        else if (_control is not null)
        {
            var control = _control;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(control.Dispose);
        }
    }

    private void SetState(SessionState next)
    {
        var prev = _state;
        _state = next;
        if (prev != next)
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs { Previous = prev, Current = next });
    }

    private static string? GetSetting(Dictionary<string, System.Text.Json.JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    internal static bool GetBoolSetting(Dictionary<string, System.Text.Json.JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) &&
        v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False &&
        v.GetBoolean();

    /// <summary>
    /// Parses the "redirectedDrives" setting (a JSON array of drive names, e.g. <c>["C:\\", "D:\\"]</c>)
    /// into the list <see cref="ApplyDriveRedirectionSelection"/> checks each drive's name against.
    /// Empty (not an error) when the setting is absent, not an array, or every element is a non-string/
    /// empty value — matches this codebase's general "missing/malformed setting = default behavior,
    /// not a thrown exception" convention.
    /// </summary>
    internal static IReadOnlyList<string> ParseRedirectedDriveLetters(Dictionary<string, System.Text.Json.JsonElement> settings)
    {
        if (!settings.TryGetValue("redirectedDrives", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                result.Add(s);
        return result;
    }

    /// <summary>Maps the "audioMode" setting to the control's own AudioRedirectionMode values (see the
    /// live-probe comment in <see cref="ConnectCore"/>). Unrecognized or missing values default to
    /// "Local", matching the control's own out-of-box default.</summary>
    internal static uint AudioModeToValue(string? audioMode) => audioMode switch
    {
        "Remote"   => 1,
        "Disabled" => 2,
        _          => 0,
    };
}
