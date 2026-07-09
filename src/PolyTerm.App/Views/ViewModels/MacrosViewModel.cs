using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.App.Macros;

namespace PolyTerm.App.ViewModels;

/// <summary>Backs the Macros window: lists the saved macros, edits them via a code-behind-wired dialog, and runs them against the active session or a broadcast.</summary>
public sealed partial class MacrosViewModel : ViewModelBase
{
    private readonly Action<IReadOnlyList<MacroDefinition>> _save;

    [ObservableProperty]
    private ObservableCollection<MacroDefinition> _macros;

    [ObservableProperty]
    private string? _error;

    // Set by MacrosWindow.OnDataContextChanged to avoid pulling Window into the VM
    public Func<MacroDefinition?, Task<MacroDefinition?>>? EditMacroAsync { get; set; }

    /// <summary>Expands and sends the macro to the active session only. Returns false if there's no active session with a terminal.</summary>
    public Func<MacroDefinition, Task<bool>>? SendToActiveSessionAsync { get; set; }

    /// <summary>Expands and broadcasts the macro to all sessions. Returns false if broadcast mode isn't armed in the main window.</summary>
    public Func<MacroDefinition, Task<bool>>? BroadcastAsync { get; set; }

    /// <summary>
    /// Takes the initial macro list and a save callback explicitly (rather than calling
    /// <see cref="MacroStore.Load"/>/<see cref="MacroStore.Save"/> directly, the way this
    /// constructor originally did) so the CRUD logic below is testable without touching the real
    /// <c>%AppData%\PolyTerm\macros.json</c> — mirrors <c>TunnelsViewModel</c>'s constructor-injected
    /// editor-delegate pattern. Real usage (<c>MainWindow.axaml.cs</c>) passes
    /// <c>MacroStore.Load()</c>/<c>MacroStore.Save</c> unchanged; tests pass an in-memory list and a
    /// no-op or recording callback instead.
    /// </summary>
    public MacrosViewModel(IReadOnlyList<MacroDefinition> initial, Action<IReadOnlyList<MacroDefinition>> save)
    {
        _macros = new ObservableCollection<MacroDefinition>(initial);
        _save   = save;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (EditMacroAsync is null) return;
        var result = await EditMacroAsync(null).ConfigureAwait(true);
        if (result is null) return;
        Macros.Add(result);
        Persist();
    }

    public async Task EditAsync(MacroDefinition existing)
    {
        if (EditMacroAsync is null) return;
        var result = await EditMacroAsync(existing).ConfigureAwait(true);
        if (result is null) return;

        var idx = Macros.IndexOf(existing);
        if (idx >= 0) Macros[idx] = result;
        Persist();
    }

    public void Delete(MacroDefinition existing)
    {
        Macros.Remove(existing);
        Persist();
    }

    public async Task RunAsync(MacroDefinition macro)
    {
        Error = null;
        if (SendToActiveSessionAsync is null || !await SendToActiveSessionAsync(macro).ConfigureAwait(true))
            Error = "No active session to send to.";
    }

    public async Task BroadcastMacroAsync(MacroDefinition macro)
    {
        Error = null;
        if (BroadcastAsync is null || !await BroadcastAsync(macro).ConfigureAwait(true))
            Error = "Enable broadcast mode in the main window first.";
    }

    private void Persist() => _save(Macros.ToList());
}
