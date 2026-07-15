using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// The main window's view model: owns the open session tabs, the connection tree, input broadcast
/// across tabs, and transient banners for connection errors and import results.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly ISessionManager _sessions;

    [ObservableProperty] private ObservableCollection<SessionTabViewModel> _tabs = [];
    [ObservableProperty] private SessionTabViewModel? _activeTab;

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

    /// <summary>Disconnects and closes <paramref name="tab"/>'s session; removal from <see cref="Tabs"/> happens via the resulting <see cref="ISessionManager.SessionClosed"/> event.</summary>
    [RelayCommand]
    private static void CloseTab(SessionTabViewModel tab)
    {
        _ = tab.CloseAsync();
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

        var tab = new SessionTabViewModel(e.Session);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ConnectionError = null;
            Tabs.Add(tab);
            ActiveTab = tab;
        });
    }

    /// <summary>Removes the tab matching the closed session from <see cref="Tabs"/>, disposes it, and reassigns <see cref="ActiveTab"/>.</summary>
    private void OnSessionClosed(object? sender, SessionEventArgs e)
    {
        var tab = Tabs.FirstOrDefault(t => t.SessionId == e.Session.SessionId);
        if (tab is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Tabs.Remove(tab);
            tab.Dispose();
            ActiveTab = Tabs.LastOrDefault();
        });
    }
}
