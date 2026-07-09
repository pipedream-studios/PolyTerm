using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>Backs the "Rename connection" dialog, letting the user edit a single profile's display name.</summary>
public sealed partial class RenameViewModel : ViewModelBase
{
    private readonly IProfileStore _store;
    private readonly ConnectionProfile _profile;
    private readonly ConnectionTreeViewModel _tree;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    /// <summary>True once <see cref="SaveAsync"/> has committed the rename; checked by the view's close handler.</summary>
    public bool Saved { get; private set; }
    public event EventHandler? RequestClose;

    public RenameViewModel(IProfileStore store, ConnectionProfile profile, ConnectionTreeViewModel tree)
    {
        _store   = store;
        _profile = profile;
        _tree    = tree;
        _name    = profile.Name;
    }

    /// <summary>Persists the new name to the profile store and reloads the connection tree so the change is reflected in the UI.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        _profile.Name = Name.Trim();
        await _store.SaveAsync(_profile).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            async () => await _tree.LoadCommand.ExecuteAsync(null));
        Saved = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);
}
