using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.Engine;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the tunnel list editor used inside the New Connection dialog: shows the current set of
/// port forwards for a profile and delegates add/edit of a single tunnel to <see cref="EditTunnelAsync"/>.
/// </summary>
public sealed partial class TunnelsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<TunnelDefinition> _tunnels;

    /// <summary>Set by TunnelsWindow.OnDataContextChanged to avoid pulling Window into the VM. Opens the single-tunnel add/edit dialog and returns the result, or null if cancelled.</summary>
    public Func<TunnelDefinition?, Task<TunnelDefinition?>>? EditTunnelAsync { get; set; }

    public TunnelsViewModel(IReadOnlyList<TunnelDefinition> initial)
    {
        _tunnels = new ObservableCollection<TunnelDefinition>(initial);
    }

    /// <summary>Opens the add-tunnel dialog and appends the result to <see cref="Tunnels"/> if confirmed.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (EditTunnelAsync is null) return;
        var result = await EditTunnelAsync(null).ConfigureAwait(true);
        if (result is not null) Tunnels.Add(result);
    }

    /// <summary>Opens the edit-tunnel dialog for <paramref name="existing"/> and replaces it in <see cref="Tunnels"/> if confirmed.</summary>
    public async Task EditAsync(TunnelDefinition existing)
    {
        if (EditTunnelAsync is null) return;
        var result = await EditTunnelAsync(existing).ConfigureAwait(true);
        if (result is null) return;

        var idx = Tunnels.IndexOf(existing);
        if (idx >= 0) Tunnels[idx] = result;
    }

    public void Delete(TunnelDefinition existing) => Tunnels.Remove(existing);

    /// <summary>Snapshot of the current tunnel list, returned to the caller when the New Connection dialog saves.</summary>
    public List<TunnelDefinition> Result => Tunnels.ToList();
}
