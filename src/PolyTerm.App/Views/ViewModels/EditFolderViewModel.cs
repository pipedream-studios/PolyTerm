using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>Backs the "Edit Folder" dialog: a folder's name and which folder it lives in. Folders have no host or protocol, so they don't use the connection dialog.</summary>
public sealed partial class EditFolderViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly ConnectionProfile _folder;
    private readonly ConnectionTreeViewModel _tree;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty] private IReadOnlyList<FolderItem> _parents = [];
    [ObservableProperty] private FolderItem? _selectedParent;

    /// <summary>True once <see cref="SaveAsync"/> has committed the change.</summary>
    public bool Saved { get; private set; }
    public event EventHandler? RequestClose;

    public EditFolderViewModel(IProfileStore store, ConnectionProfile folder, ConnectionTreeViewModel tree)
    {
        _store  = store;
        _folder = folder;
        _tree   = tree;
        _name   = folder.Name;
        _ = LoadParentsAsync();
    }

    private async Task LoadParentsAsync()
    {
        var all = await _store.GetAllAsync().ConfigureAwait(false);
        var parents = BuildParentChoices(all, _folder);
        var selected = parents.FirstOrDefault(f => f.Id == _folder.ParentId) ?? parents[0];
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Parents        = parents;
            SelectedParent = selected;
        });
    }

    /// <summary>
    /// Root plus every folder the edited folder may be moved into: all folders except itself and its own
    /// descendants (moving a folder inside its own subtree would detach it from the tree).
    /// </summary>
    internal static IReadOnlyList<FolderItem> BuildParentChoices(IReadOnlyList<ConnectionProfile> all, ConnectionProfile folder)
    {
        var byId = all.ToDictionary(p => p.Id);
        var excluded = new HashSet<Guid> { folder.Id };
        bool grew;
        do
        {
            grew = false;
            foreach (var p in all)
                if (p.IsFolder && !excluded.Contains(p.Id) && p.ParentId is { } pid && excluded.Contains(pid))
                    grew |= excluded.Add(p.Id);
        } while (grew);

        var items = new List<FolderItem> { new(null, "— Root —") };
        items.AddRange(all.Where(p => p.IsFolder && !excluded.Contains(p.Id))
            .Select(p => new FolderItem(p.Id, Path(p, byId)))
            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase));
        return items;
    }

    private static string Path(ConnectionProfile folder, IReadOnlyDictionary<Guid, ConnectionProfile> byId)
    {
        var parts = new List<string>();
        var seen = new HashSet<Guid>();
        for (var current = folder; current is not null && seen.Add(current.Id);
             current = current.ParentId is { } pid && byId.TryGetValue(pid, out var p) ? p : null)
            parts.Insert(0, current.Name);
        return string.Join(" / ", parts);
    }

    /// <summary>Persists the name and parent and reloads the connection tree.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        _folder.Name     = Name.Trim();
        _folder.ParentId = SelectedParent?.Id;
        await _store.SaveAsync(_folder).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await _tree.LoadCommand.ExecuteAsync(null);
            Saved = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        });
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);
}
