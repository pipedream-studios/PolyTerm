using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.App.Serial;
using PolyTerm.Engine;
using PolyTerm.Protocols.Serial;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the connection list/tree panel: loads <see cref="ConnectionProfile"/> entries (and folders)
/// from the profile store into a hierarchy of <see cref="ConnectionNodeViewModel"/> nodes, and opens
/// sessions via <see cref="ISessionManager"/> when the user connects. Also appends dynamic,
/// never-persisted nodes for detected USB COM ports at the bottom of the tree (issue #33) — see
/// <see cref="BuildDetectedPortNodes"/>.
/// </summary>
public sealed partial class ConnectionTreeViewModel : ViewModelBase
{
    /// <summary>Above this many detected ports, group them under one folder node instead of listing them flat.</summary>
    private const int InlineDetectedPortLimit = 4;

    private readonly IProfileStore _store;
    private readonly ISessionManager _sessions;
    private readonly IProtocolRegistry _registry;
    private readonly IComPortWatcher _portWatcher;

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

    public ConnectionTreeViewModel(IProfileStore store, ISessionManager sessions, IProtocolRegistry registry, IComPortWatcher portWatcher)
    {
        _store       = store;
        _sessions    = sessions;
        _registry    = registry;
        _portWatcher = portWatcher;

        // Raised on a background thread (ComPortWatcherService's poll timer) - LoadCommand.ExecuteAsync
        // is safe to fire-and-forget from there since LoadAsync's own RootNodes assignment already
        // marshals to the UI thread (Dispatcher.UIThread.Post below).
        _portWatcher.PortsChanged += (_, _) => _ = LoadCommand.ExecuteAsync(null);
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

    /// <summary>
    /// The icon for a connection node of this protocol (issue #35), e.g. "🔐" for ssh. Falls back to
    /// the old generic desktop glyph for an unregistered protocol id, matching
    /// <see cref="IsProtocolAvailable"/>'s own treat-unknown-permissively precedent.
    /// </summary>
    internal string GetProtocolIcon(string protocolId) => _registry.Find(protocolId)?.Icon ?? "🖥";

    /// <summary>Reloads all profiles from the store and rebuilds <see cref="RootNodes"/> as a folder/connection hierarchy, with detected USB COM ports appended at the bottom (issue #33).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        var all   = await _store.GetAllAsync().ConfigureAwait(false);
        var roots = BuildTree(all, parentId: null).Concat(BuildDetectedPortNodes()).ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Carry expanded folders and the selection across the rebuild so a reload (edit, delete,
            // COM port change...) doesn't collapse the tree back to root level.
            var expanded   = new HashSet<Guid>(Flatten(RootNodes).Where(n => n is { IsFolder: true, IsAdHoc: false, IsExpanded: true }).Select(n => n.Profile.Id));
            var selectedId = SelectedNode is { IsAdHoc: false } sel ? sel.Profile.Id : (Guid?)null;

            var flat = Flatten(roots).Where(n => !n.IsAdHoc).ToList();
            foreach (var node in flat.Where(n => n.IsFolder && expanded.Contains(n.Profile.Id)))
                node.IsExpanded = true;

            RootNodes    = [.. roots];
            SelectedNode = selectedId is { } id ? flat.FirstOrDefault(n => n.Profile.Id == id) : null;
        });
    }

    private static IEnumerable<ConnectionNodeViewModel> Flatten(IEnumerable<ConnectionNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

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

    /// <summary>
    /// Builds dynamic, never-persisted nodes for <see cref="IComPortWatcher.CurrentPorts"/> — nothing
    /// at all when no ports are detected; each port as its own flat node when there are
    /// <see cref="InlineDetectedPortLimit"/> or fewer; otherwise all of them nested under a single
    /// "Detected COM Ports" folder node, to keep the tree from getting cluttered (issue #33). Always
    /// appended after the real profile tree, so these stay at the bottom regardless of alphabetical
    /// sorting elsewhere in <see cref="BuildTree"/>.
    /// </summary>
    private IReadOnlyList<ConnectionNodeViewModel> BuildDetectedPortNodes()
    {
        var ports = _portWatcher.CurrentPorts;
        if (ports.Count == 0) return [];

        var portNodes = ports
            .OrderBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ConnectionNodeViewModel(BuildAdHocSerialProfile(p), this, isAdHoc: true))
            .ToList();

        if (portNodes.Count <= InlineDetectedPortLimit)
            return portNodes;

        var folder = new ConnectionNodeViewModel(
            new ConnectionProfile { Name = $"Detected COM Ports ({portNodes.Count})", IsFolder = true },
            this, isAdHoc: true);
        foreach (var node in portNodes)
            folder.Children.Add(node);
        return [folder];
    }

    /// <summary>
    /// An in-memory-only Serial profile for a detected port — never passed to <see cref="IProfileStore.SaveAsync"/>.
    /// Only <c>portName</c> is set; baud rate/data bits/parity/stop bits are left absent so
    /// <c>SerialSession.ConnectAsync</c>'s own defaults (9600/8/None/One) apply, the same defaults
    /// <c>NewConnectionViewModel</c> pre-fills for a brand-new Serial profile.
    /// </summary>
    private static ConnectionProfile BuildAdHocSerialProfile(DetectedComPort port) => new()
    {
        Name       = port.FriendlyName,
        ProtocolId = "serial",
        Host       = port.PortName,
        Settings   = new Dictionary<string, JsonElement>
        {
            ["portName"] = JsonSerializer.SerializeToElement(port.PortName),
        },
    };

    /// <summary>Opens a session for <see cref="SelectedNode"/> if it's a connection (not a folder).</summary>
    [RelayCommand]
    public async Task ConnectSelectedAsync()
    {
        if (SelectedNode is not { IsFolder: false } node) return;
        await ConnectNodeAsync(node).ConfigureAwait(false);
    }

    // ── Called by ConnectionNodeViewModel commands ───────────────────────────

    /// <summary>
    /// Opens a session for <paramref name="node"/>. Ad-hoc nodes (detected USB COM ports — issue #33)
    /// were never saved to the profile store, so they connect via <see cref="ISessionManager.OpenDirectAsync"/>
    /// with the node's own in-memory <see cref="ConnectionProfile"/> instead of resolving by id.
    /// </summary>
    internal Task ConnectNodeAsync(ConnectionNodeViewModel node) =>
        node.IsAdHoc ? _sessions.OpenDirectAsync(node.Profile) : _sessions.OpenAsync(node.Profile.Id);

    /// <summary>Deletes the node's profile from the store and reloads the tree. A no-op for ad-hoc nodes (issue #33) — they were never saved, so there's nothing to delete.</summary>
    internal async Task DeleteNodeAsync(ConnectionNodeViewModel node)
    {
        if (node.IsAdHoc) return;
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

    /// <summary>
    /// True for a dynamic, never-persisted node — a detected USB COM port, or the folder grouping
    /// more than 4 of them (issue #33). Never saved to <see cref="IProfileStore"/>, so Edit/Rename/
    /// Delete/Duplicate don't apply, and connecting goes through <see cref="ISessionManager.OpenDirectAsync"/>
    /// instead of resolving <see cref="Profile"/>'s id from the store.
    /// </summary>
    public bool IsAdHoc { get; }

    public string Icon => IsFolder ? "🗂" : IsAdHoc ? "🔌" : _tree.GetProtocolIcon(Profile.ProtocolId);
    public bool CanConnect   => !IsFolder && _tree.IsProtocolAvailable(Profile.ProtocolId, out _);
    public bool CanDuplicate => !IsFolder && !IsAdHoc;

    /// <summary>Whether Edit/Rename/Delete apply to this node — false for ad-hoc nodes (issue #33), which were never saved and have nothing to edit, rename, or delete.</summary>
    public bool CanModify => !IsAdHoc;

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

    /// <summary>Whether this folder is expanded in the tree; two-way bound to the TreeViewItem so it survives <see cref="ConnectionTreeViewModel.LoadAsync"/> rebuilds.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public ConnectionNodeViewModel(ConnectionProfile profile, ConnectionTreeViewModel tree, bool isAdHoc = false)
    {
        Profile  = profile;
        _tree    = tree;
        IsAdHoc  = isAdHoc;
    }

    /// <summary>Opens a session for this node's profile; connection failures are reported via <see cref="ConnectionTreeViewModel.ErrorOccurred"/> rather than thrown.</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        try { await _tree.ConnectNodeAsync(this).ConfigureAwait(false); }
        catch (Exception ex) { _tree.NotifyError(ex.Message); }
    }

    [RelayCommand]
    private Task DeleteAsync() => CanModify ? _tree.DeleteNodeAsync(this) : Task.CompletedTask;

    [RelayCommand]
    private Task DuplicateAsync() => CanDuplicate ? _tree.DuplicateNodeAsync(Profile) : Task.CompletedTask;

    [RelayCommand]
    private void Edit()
    {
        if (CanModify) _tree.RequestEdit(Profile);
    }

    [RelayCommand]
    private void Rename()
    {
        if (CanModify) _tree.RequestRename(Profile);
    }
}
