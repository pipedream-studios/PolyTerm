using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>Outcome of <see cref="MainViewModel.ConfirmCloseAsync"/>'s "On Close = Ask" prompt (issue #77 Phase 1).</summary>
public enum CloseSessionChoice { Close, Detach, Cancel }

/// <summary>A detached-but-still-live session, shown in the reattach picker (issue #77 Phase 1).</summary>
public sealed record DetachedSessionItem(Guid SessionId, string DisplayName);

/// <summary>
/// The main window's view model: owns the open session tabs, the connection tree, input broadcast
/// across tabs, and transient banners for connection errors and import results.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;

    [ObservableProperty] private ObservableCollection<SessionTabViewModel> _tabs = [];
    [ObservableProperty] private SessionTabViewModel? _activeTab;

    /// <summary>
    /// Sessions that survived their tab closing (issue #77 Phase 1 — "On Close" = Detach) and can be
    /// reopened as a tab again via <see cref="ReattachAsync"/>. Maintained explicitly alongside
    /// <see cref="Tabs"/>, not recomputed from <see cref="ISessionManager.ActiveSessions"/> on every
    /// access — same "own the collection, update it at the specific points it changes" convention
    /// already used for <see cref="Tabs"/> itself.
    /// </summary>
    [ObservableProperty] private ObservableCollection<DetachedSessionItem> _detachedSessions = [];

    /// <summary>
    /// Opens <see cref="Views.CloseSessionDialog"/> and returns the user's choice - set by
    /// <c>MainWindow.OnDataContextChanged</c>, since the ViewModel itself can't own a Window reference
    /// (same pattern as <see cref="NewConnectionViewModel.OpenCredentialPickerAsync"/>). Null in
    /// contexts with no view wired up (e.g. headless tests) - treated as "Cancel" by
    /// <see cref="CloseTabAsync"/> rather than crashing.
    /// </summary>
    public Func<string, Task<CloseSessionChoice>>? ConfirmCloseAsync { get; set; }

    /// <summary>Opens a picker over <see cref="DetachedSessions"/> and returns the chosen one, or null if cancelled/empty. Same View-wiring pattern as <see cref="ConfirmCloseAsync"/>.</summary>
    public Func<IReadOnlyList<DetachedSessionItem>, Task<DetachedSessionItem?>>? PickDetachedSessionAsync { get; set; }

    /// <summary>When true, text submitted via <see cref="BroadcastAsync"/> is sent to every open tab's terminal instead of just the active one.</summary>
    [ObservableProperty] private bool _broadcastEnabled;

    /// <summary>Gates the connection tree sidebar's visibility — View &gt; Connection Tree (issue #74). Not persisted: resets to visible on each launch, matching this being a quick show/hide toggle rather than a saved layout preference.</summary>
    [ObservableProperty] private bool _connectionTreeVisible = true;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasConnectionError))]
    private string? _connectionError;
    public bool HasConnectionError => ConnectionError is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportResult))]
    private string? _importResult;
    public bool HasImportResult => ImportResult is not null;

    /// <summary>Transient status text for direct-connect paths (quick connect, new local shell) that don't go through a tab's own busy tracking.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasConnectingStatus))]
    private string? _connectingStatus;
    public bool HasConnectingStatus => ConnectingStatus is not null;

    public ConnectionTreeViewModel ConnectionTree { get; }

    public MainViewModel(ISessionManager sessions, ConnectionTreeViewModel tree)
    {
        _sessions      = sessions;
        ConnectionTree = tree;

        _sessions.SessionOpened += OnSessionOpened;
        _sessions.SessionClosed += OnSessionClosed;
        ConnectionTree.ErrorOccurred += OnConnectionError;

        _ = ConnectionTree.LoadCommand.ExecuteAsync(null);
    }

    private void OnConnectionError(object? sender, string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ConnectionError = message);

    /// <summary>
    /// Closes or detaches <paramref name="tab"/>'s session per its profile's "On Close" setting
    /// (issue #77 Phase 1): "Close" disconnects (today's only behavior before this - removal from
    /// <see cref="Tabs"/> happens via the resulting <see cref="ISessionManager.SessionClosed"/> event,
    /// unchanged); "Detach" leaves the session running and just removes the tab; "Ask" shows
    /// <see cref="ConfirmCloseAsync"/>'s dialog first and acts on the result (Cancel does nothing).
    /// </summary>
    [RelayCommand]
    private async Task CloseTabAsync(SessionTabViewModel tab)
    {
        var onClose = GetSetting(tab.Profile.Settings, "onClose") ?? "Ask";
        if (onClose == "Ask")
        {
            // No dialog wired up (e.g. a headless test) - fall back to the pre-#77 always-close
            // behavior rather than silently doing nothing.
            var choice = ConfirmCloseAsync is null
                ? CloseSessionChoice.Close
                : await ConfirmCloseAsync(tab.Title).ConfigureAwait(true);
            onClose = choice switch
            {
                CloseSessionChoice.Close  => "Close",
                CloseSessionChoice.Detach => "Detach",
                _                         => "Cancel",
            };
        }

        switch (onClose)
        {
            case "Cancel":
                return;
            case "Detach":
                DetachTab(tab);
                return;
            default:
                // Not tab.CloseAsync() - that routes back through OnCloseRequested into this same
                // method, which would recurse. Go straight to the session manager instead; tab
                // removal still happens via the SessionClosed event this triggers, unchanged.
                await _sessions.CloseAsync(tab.SessionId).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Removes <paramref name="tab"/> from <see cref="Tabs"/> and disposes it (unsubscribing from the
    /// session's events, same cleanup <see cref="OnSessionClosed"/> already does) without touching the
    /// underlying session at all - it stays exactly as-is in <see cref="ISessionManager.ActiveSessions"/>,
    /// which is the entire mechanism: nothing else forces disconnection on tab removal. Lists the
    /// session in <see cref="DetachedSessions"/> so <see cref="ReattachAsync"/> can find it again.
    /// </summary>
    private void DetachTab(SessionTabViewModel tab)
    {
        var sessionId = tab.SessionId;
        var displayName = tab.Title;
        Tabs.Remove(tab);
        tab.Dispose();
        ActiveTab = Tabs.LastOrDefault();
        DetachedSessions.Add(new DetachedSessionItem(sessionId, displayName));
    }

    /// <summary>Opens the reattach picker over <see cref="DetachedSessions"/> and, if one was chosen, wraps its still-live <see cref="IProtocolSession"/> in a brand-new tab - reusing the existing session object, not opening a new connection.</summary>
    [RelayCommand]
    private async Task ReattachAsync()
    {
        if (PickDetachedSessionAsync is null || DetachedSessions.Count == 0) return;

        var picked = await PickDetachedSessionAsync(DetachedSessions).ConfigureAwait(true);
        if (picked is null) return;

        var session = _sessions.ActiveSessions.FirstOrDefault(s => s.SessionId == picked.SessionId);
        DetachedSessions.Remove(picked);
        if (session is null) return; // it disconnected on its own between listing and picking - OnSessionClosed already cleaned it up

        var tab = new SessionTabViewModel(session) { OnCloseRequested = t => CloseTabAsync(t) };
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    private void DismissError() => ConnectionError = null;

    [RelayCommand]
    private void DismissImport() => ImportResult = null;

    [RelayCommand]
    private void ToggleBroadcast() => BroadcastEnabled = !BroadcastEnabled;

    /// <summary>Sends <paramref name="text"/> to every open tab that has a terminal, when <see cref="BroadcastEnabled"/> is on; otherwise a no-op.</summary>
    [RelayCommand]
    private async Task BroadcastAsync(string text)
    {
        if (!BroadcastEnabled) return;
        var targets = Tabs
            .Where(t => t.Terminal is not null)
            .Select(t => t.SessionId);
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        await _sessions.BroadcastInputAsync(data, targets).ConfigureAwait(false);
    }

    /// <summary>Wraps the newly opened session in a tab, adds it to <see cref="Tabs"/>, and makes it the <see cref="ActiveTab"/>.</summary>
    private void OnSessionOpened(object? sender, SessionEventArgs e)
    {
        // SurfaceKind.External sessions have no capability to render a tab for — this is now only
        // HTTP/S profiles with "openExternally" set (Process.Start already produced the session's
        // real "surface", the OS browser window) or any future genuinely surfaceless protocol.
        // There's nothing left for this app's own window to track. Close it immediately rather than
        // leaving it open forever in SessionManager with no UI ever able to close it.
        if (e.Session is { Terminal: null, FileTransfer: null, Framebuffer: null, NativeHost: null, Web: null })
        {
            _ = e.Session.DisposeAsync().AsTask();
            return;
        }

        var tab = new SessionTabViewModel(e.Session) { OnCloseRequested = t => CloseTabAsync(t) };
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConnectionError = null;
            Tabs.Add(tab);
            ActiveTab = tab;
        });
    }

    /// <summary>
    /// Removes the tab matching the closed session from <see cref="Tabs"/>, disposes it, and
    /// reassigns <see cref="ActiveTab"/> - or, if the session had no tab (detached), just removes it
    /// from <see cref="DetachedSessions"/> instead. A detached session can still genuinely disconnect
    /// on its own (the remote end drops it) with nothing watching it, so this has to handle both cases.
    /// </summary>
    private void OnSessionClosed(object? sender, SessionEventArgs e)
    {
        var tab = Tabs.FirstOrDefault(t => t.SessionId == e.Session.SessionId);
        if (tab is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Tabs.Remove(tab);
                tab.Dispose();
                ActiveTab = Tabs.LastOrDefault();
            });
            return;
        }

        var detached = DetachedSessions.FirstOrDefault(d => d.SessionId == e.Session.SessionId);
        if (detached is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => DetachedSessions.Remove(detached));
    }

    private static string? GetSetting(Dictionary<string, JsonElement> settings, string key) =>
        settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
