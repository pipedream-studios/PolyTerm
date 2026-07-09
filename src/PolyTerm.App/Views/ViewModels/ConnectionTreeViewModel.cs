using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the connection list/tree panel: loads <see cref="ConnectionProfile"/> entries (and folders)
/// from the profile store into a hierarchy of <see cref="ConnectionNodeViewModel"/> nodes, and opens
/// sessions via <see cref="ISessionManager"/> when the user connects.
/// </summary>
public sealed partial class ConnectionTreeViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly ISessionManager _sessions;
    private readonly IProtocolRegistry _registry;

    [ObservableProperty]
    private ObservableCollection<ConnectionNodeViewModel> _rootNodes = [];

    [ObservableProperty]
    private ConnectionNodeViewModel? _selectedNode;

    /// <summary>Raised when the user chooses Edit from a node's context menu.</summary>
    public event EventHandler<ConnectionProfile>? EditRequested;

    /// <summary>Raised when the user chooses Rename from a node's context menu.</summary>
    public event EventHandler<ConnectionProfile>? RenameRequested;

    /// <summary>Raised when a connection attempt fails — carries the error message.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Raises <see cref="ErrorOccurred"/>; called by child <see cref="ConnectionNodeViewModel"/> commands that can't raise it directly.</summary>
    internal void NotifyError(string message) =>
        ErrorOccurred?.Invoke(this, message);

    public ConnectionTreeViewModel(IProfileStore store, ISessionManager sessions, IProtocolRegistry registry)
    {
        _store    = store;
        _sessions = sessions;
        _registry = registry;
    }

    /// <summary>
    /// Whether <paramref name="protocolId"/>'s module is usable in this environment (e.g. RDP's
    /// native control is Windows-only). An unregistered protocol id is treated as available so the
    /// connect attempt itself surfaces the real "unknown protocol" error, rather than this gate.
    /// </summary>
    internal bool IsProtocolAvailable(string protocolId, out string? reason)
    {
        var descriptor = _registry.Find(protocolId);
        if (descriptor is null)
        {
            reason = null;
            return true;
        }
        return descriptor.IsAvailable(out reason);
    }

    /// <summary>Reloads all profiles from the store and rebuilds <see cref="RootNodes"/> as a folder/connection hierarchy.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var all   = await _store.GetAllAsync().ConfigureAwait(false);
        var roots = BuildTree(all, parentId: null);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RootNodes = [.. roots]);
    }

    /// <summary>Recursively builds the node tree for children of <paramref name="parentId"/> (null selects root-level entries), folders first, then alphabetically.</summary>
    private IEnumerable<ConnectionNodeViewModel> BuildTree(
        IReadOnlyList<ConnectionProfile> all, Guid? parentId)
    {
        return all
            .Where(p => p.ParentId == parentId)
            .OrderByDescending(p => p.IsFolder)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var node = new ConnectionNodeViewModel(p, this);
                foreach (var child in BuildTree(all, p.Id))
                    node.Children.Add(child);
                return node;
            });
    }

    /// <summary>Opens a session for <see cref="SelectedNode"/> if it's a connection (not a folder).</summary>
    [RelayCommand]
    public async Task ConnectSelectedAsync()
    {
        if (SelectedNode?.Profile is not { IsFolder: false } profile) return;
        await _sessions.OpenAsync(profile.Id).ConfigureAwait(false);
    }

    // ── Called by ConnectionNodeViewModel commands ───────────────────────────

    internal Task ConnectNodeAsync(ConnectionProfile profile) =>
        _sessions.OpenAsync(profile.Id);

    /// <summary>Deletes the node's profile from the store and reloads the tree.</summary>
    internal async Task DeleteNodeAsync(ConnectionNodeViewModel node)
    {
        await _store.DeleteAsync(node.Profile.Id).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>Saves a copy of <paramref name="profile"/> (named "&lt;name&gt; (copy)", same folder) and reloads the tree.</summary>
    internal async Task DuplicateNodeAsync(ConnectionProfile profile)
    {
        var copy = new ConnectionProfile
        {
            Name       = profile.Name + " (copy)",
            ProtocolId = profile.ProtocolId,
            Host       = profile.Host,
            Port       = profile.Port,
            Settings   = new Dictionary<string, System.Text.Json.JsonElement>(profile.Settings),
            ParentId   = profile.ParentId,
        };
        await _store.SaveAsync(copy).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    internal void RequestEdit(ConnectionProfile profile) =>
        EditRequested?.Invoke(this, profile);

    internal void RequestRename(ConnectionProfile profile) =>
        RenameRequested?.Invoke(this, profile);
}

/// <summary>A single node (folder or connection) in <see cref="ConnectionTreeViewModel.RootNodes"/>, wrapping its <see cref="ConnectionProfile"/> and child nodes.</summary>
public sealed partial class ConnectionNodeViewModel : ViewModelBase
{
    private readonly ConnectionTreeViewModel _tree;

    public ConnectionProfile Profile { get; }
    public string Name    => Profile.Name;
    public bool IsFolder  => Profile.IsFolder;
    public string Icon    => IsFolder ? "🗂" : "🖥";
    public bool CanConnect   => !IsFolder && _tree.IsProtocolAvailable(Profile.ProtocolId, out _);
    public bool CanDuplicate => !IsFolder;

    /// <summary>Why <see cref="CanConnect"/> is false due to protocol unavailability (e.g. RDP on non-Windows), for a row tooltip. Null when connectable or when it's a folder.</summary>
    public string? UnavailableReason
    {
        get
        {
            if (IsFolder) return null;
            _tree.IsProtocolAvailable(Profile.ProtocolId, out var reason);
            return reason;
        }
    }

    public ObservableCollection<ConnectionNodeViewModel> Children { get; } = [];

    public ConnectionNodeViewModel(ConnectionProfile profile, ConnectionTreeViewModel tree)
    {
        Profile = profile;
        _tree   = tree;
    }

    /// <summary>Opens a session for this node's profile; connection failures are reported via <see cref="ConnectionTreeViewModel.ErrorOccurred"/> rather than thrown.</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        try { await _tree.ConnectNodeAsync(Profile).ConfigureAwait(false); }
        catch (Exception ex) { _tree.NotifyError(ex.Message); }
    }

    [RelayCommand]
    private Task DeleteAsync() => _tree.DeleteNodeAsync(this);

    [RelayCommand]
    private Task DuplicateAsync() => _tree.DuplicateNodeAsync(Profile);

    [RelayCommand]
    private void Edit() => _tree.RequestEdit(Profile);

    [RelayCommand]
    private void Rename() => _tree.RequestRename(Profile);
}
