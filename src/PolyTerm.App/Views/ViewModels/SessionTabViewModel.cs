using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.App.Recording;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Represents one open session tab in the main window: wraps an <see cref="IProtocolSession"/>,
/// tracks its connection-state title and OSC title, hosts an optional SFTP panel, and manages
/// optional output recording.
/// </summary>
public sealed partial class SessionTabViewModel : ViewModelBase, IDisposable
{
    private readonly IProtocolSession _session;

    // Profile-name + connection-state label (e.g. "server (connecting…)")
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _connectionTitle;

    // OSC 0/1/2 title received from the remote process via TerminalSurface.EmulatorTitle binding.
    // Overrides ConnectionTitle while non-empty; cleared when the session disconnects.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _oscTitle = "";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Message from the most recent <see cref="IProtocolSession.ErrorOccurred"/>, or null if none has
    /// fired yet. Previously a Faulted session only changed the tab title to "✗" with no way to see
    /// why — this is shown as the tab's tooltip so the actual error is at least reachable by hovering.
    /// </summary>
    [ObservableProperty] private string? _lastErrorMessage;

    /// <summary>
    /// True once a native-host session (RDP today) has confirmed the connected server doesn't
    /// support live resolution renegotiation — the desktop is letterboxed (scaled to fit, not
    /// actually resized) instead. Null/false-by-default until <see cref="INativeHostChannel.ResizeAsync"/>
    /// has actually been attempted at least once (i.e. the user has resized the tab), since there's no
    /// way to know ahead of time without trying — see docs/todos.md's RDP section.
    /// </summary>
    [ObservableProperty] private bool _liveResizeUnsupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingActive))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingActive))]
    private bool _isRecordingPaused;

    /// <summary>True while recording and not paused — i.e. when the "Pause Recording" action (as opposed to "Resume Recording") should be shown.</summary>
    public bool IsRecordingActive => IsRecording && !IsRecordingPaused;

    private ISessionRecorder? _recorder;

    /// <summary>
    /// Shown in the tab header and status bar. OSC title takes precedence when set;
    /// falls back to the connection-state label otherwise.
    /// </summary>
    public string Title => string.IsNullOrEmpty(OscTitle) ? ConnectionTitle : OscTitle;

    private SftpBrowserViewModel? _sftpBrowser;
    public SftpBrowserViewModel? SftpBrowser => _sftpBrowser;
    public bool CanOpenSftp => _session.FileTransfer is not null;

    /// <summary>True for graphical-desktop sessions (RDP/VNC) — the only kinds where Ctrl+Alt+Del is meaningful.</summary>
    public bool CanSendCtrlAltDelete => _session.Framebuffer is not null || _session.NativeHost is not null;

    /// <summary>
    /// Dispatches to whichever channel this session actually has — capability-scoped the same way
    /// <c>McpBridgeService</c>'s <c>FramebufferTools</c> already are (VNC first, then RDP), rather than
    /// needing the caller to know or care which kind of graphical session this tab holds.
    /// </summary>
    public async Task SendCtrlAltDeleteAsync()
    {
        if (_session.Framebuffer is { } framebuffer)
            await framebuffer.SendCtrlAltDeleteAsync().ConfigureAwait(false);
        else if (_session.NativeHost is { } nativeHost)
            await nativeHost.SendCtrlAltDeleteAsync().ConfigureAwait(false);
    }

    [ObservableProperty] private bool _sftpPanelOpen;

    /// <summary>Opens or closes the SFTP panel, lazily creating <see cref="SftpBrowser"/> on first open.</summary>
    public void ToggleSftpPanel()
    {
        if (!CanOpenSftp) return;
        if (!SftpPanelOpen)
        {
            if (_sftpBrowser is null)
            {
                _sftpBrowser = new SftpBrowserViewModel(_session.FileTransfer!);
                OnPropertyChanged(nameof(SftpBrowser));
            }
            SftpPanelOpen = true;
        }
        else
        {
            SftpPanelOpen = false;
        }
    }

    public Guid SessionId => _session.SessionId;
    public SurfaceKind Surface { get; }
    public ITerminalChannel? Terminal => _session.Terminal;
    public INativeHostChannel? NativeHost => _session.NativeHost;
    public IFramebufferChannel? Framebuffer => _session.Framebuffer;
    public IWebChannel? Web => _session.Web;

    /// <summary>The profile this session was opened from — used e.g. by macro variable expansion.</summary>
    public ConnectionProfile Profile => _session.Profile;

    public SessionTabViewModel(IProtocolSession session)
    {
        _session        = session;
        _connectionTitle = session.Profile.Name;
        Surface         = session.NativeHost is not null ? SurfaceKind.NativeHost
            : session.Framebuffer is not null ? SurfaceKind.Framebuffer
            : session.Web is not null ? SurfaceKind.WebView
            : session.Terminal is not null ? SurfaceKind.Terminal
            : SurfaceKind.External;

        session.StateChanged   += OnStateChanged;
        session.ErrorOccurred  += OnErrorOccurred;
        if (session.NativeHost is not null)
            session.NativeHost.SupportsLiveResizeChanged += OnSupportsLiveResizeChanged;
    }

    private void OnErrorOccurred(object? sender, SessionErrorEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LastErrorMessage = e.Exception.Message);

    private void OnSupportsLiveResizeChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            LiveResizeUnsupported = NativeHost?.SupportsLiveResize == false);

    /// <summary>Disconnects the underlying session; the tab itself is removed by <c>MainViewModel</c> in response to the resulting <c>SessionClosed</c> event.</summary>
    [RelayCommand]
    public async Task CloseAsync()
    {
        IsBusy = true;
        try { await _session.DisconnectAsync().ConfigureAwait(false); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Updates <see cref="ConnectionTitle"/> for the new state and, on a reconnect cycle
    /// (transition into <see cref="SessionState.Reconnecting"/>, or out of it into
    /// <see cref="SessionState.Connected"/>), re-raises <see cref="ObservableObject.PropertyChanged"/>
    /// for <see cref="Terminal"/> and <see cref="CanOpenSftp"/>. This is necessary because the
    /// underlying <see cref="ITerminalChannel"/>/<see cref="IFileTransferChannel"/> instances are
    /// swapped out for fresh ones on reconnect, so bound views must be told to re-read them even
    /// though the properties' identity (not just value) changed. The SFTP browser is torn down when
    /// reconnecting starts and rebuilt (if the panel was open) once the session is connected again.
    /// An active recording is rebound to the fresh channel the same way, rather than being dropped.
    /// </summary>
    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConnectionTitle = e.Current switch
            {
                SessionState.Connecting   => $"{_session.Profile.Name} (connecting…)",
                SessionState.Reconnecting => $"{_session.Profile.Name} (reconnecting…)",
                SessionState.Faulted      => $"{_session.Profile.Name} ✗",
                SessionState.Closed       => $"{_session.Profile.Name} (closed)",
                _                         => _session.Profile.Name,
            };

            // Drop the OSC title when the session ends so the state label is visible
            if (e.Current is SessionState.Faulted or SessionState.Closed)
                OscTitle = "";

            // A prior error stops being relevant once the session is healthy again — a fresh connect
            // or a successful reconnect both mean whatever LastErrorMessage described is now resolved.
            if (e.Current == SessionState.Connected)
                LastErrorMessage = null;

            // Detach the UI from the dying channel immediately, and reattach to the fresh one
            // once reconnected — TerminalSurface.Reattach already tolerates a null Channel.
            if (e.Current == SessionState.Reconnecting ||
                (e.Previous == SessionState.Reconnecting && e.Current == SessionState.Connected))
            {
                OnPropertyChanged(nameof(Terminal));
                OnPropertyChanged(nameof(CanOpenSftp));

                if (e.Current == SessionState.Connected && SftpPanelOpen && _session.FileTransfer is not null)
                {
                    _sftpBrowser = new SftpBrowserViewModel(_session.FileTransfer);
                    OnPropertyChanged(nameof(SftpBrowser));
                }
                else if (e.Current == SessionState.Reconnecting)
                {
                    _sftpBrowser?.Dispose();
                    _sftpBrowser = null;
                    OnPropertyChanged(nameof(SftpBrowser));
                }

                // An active recording was tailing the old (now-dead) channel — point it at the
                // fresh one so it keeps going instead of silently going stale.
                if (e.Current == SessionState.Connected && IsRecording && _recorder is not null && Terminal is not null)
                    _recorder.Rebind(Terminal);
            }
        });
    }

    /// <summary>Starts writing terminal output to <paramref name="path"/>.</summary>
    /// <param name="asciicast">True for asciicast v2 (.cast), false for raw bytes (.log).</param>
    /// <param name="cols">Terminal width at recording start (used in asciicast header).</param>
    /// <param name="rows">Terminal height at recording start (used in asciicast header).</param>
    public void StartRecording(string path, bool asciicast, int cols = 80, int rows = 24)
    {
        if (IsRecording || Terminal is null) return;
        _recorder = asciicast
            ? new AsciicastRecorder(path, Terminal, cols, rows, ConnectionTitle)
            : new RawSessionRecorder(path, Terminal);
        IsRecording = true;
    }

    public void StopRecording()
    {
        _recorder?.Dispose();
        _recorder          = null;
        IsRecording        = false;
        IsRecordingPaused  = false;
    }

    /// <summary>Pauses an active recording — the output file stops receiving new data (e.g. before running a sensitive command) but keeps its position/timing so <see cref="ResumeRecording"/> continues rather than restarting.</summary>
    public void PauseRecording()
    {
        if (_recorder is null || IsRecordingPaused) return;
        _recorder.Pause();
        IsRecordingPaused = true;
    }

    /// <summary>Resumes a paused recording.</summary>
    public void ResumeRecording()
    {
        if (_recorder is null || !IsRecordingPaused) return;
        _recorder.Resume();
        IsRecordingPaused = false;
    }

    /// <summary>Stops any active recording, closes any open SFTP edit sessions, and unsubscribes from the session's state-change event.</summary>
    public void Dispose()
    {
        StopRecording();
        _sftpBrowser?.Dispose();
        _session.StateChanged  -= OnStateChanged;
        _session.ErrorOccurred -= OnErrorOccurred;
        if (_session.NativeHost is not null)
            _session.NativeHost.SupportsLiveResizeChanged -= OnSupportsLiveResizeChanged;
    }
}
