using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PolyTerm.App.CommandPalette;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Backs the command palette (Ctrl+Shift+P): filters <see cref="PaletteCommand"/>s by
/// <see cref="SearchText"/> via <see cref="CommandPaletteFilter"/> as the user types, tracks which
/// one is selected (arrow keys move it, Enter runs it), and exposes <see cref="Confirm"/> for the
/// view's Enter-key handling to call.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PaletteCommand> _allCommands;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ObservableCollection<PaletteCommand> _filteredCommands;
    [ObservableProperty] private PaletteCommand? _selectedCommand;

    /// <summary>Set true by <see cref="Confirm"/> once a command has actually been chosen — the view checks this after closing to decide whether to run it.</summary>
    public bool Confirmed { get; private set; }

    public CommandPaletteViewModel(IReadOnlyList<PaletteCommand> commands)
    {
        _allCommands = commands;
        _filteredCommands = new ObservableCollection<PaletteCommand>(commands);
        _selectedCommand = _filteredCommands.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredCommands = new ObservableCollection<PaletteCommand>(CommandPaletteFilter.Filter(_allCommands, value));
        SelectedCommand = FilteredCommands.FirstOrDefault();
    }

    /// <summary>Moves the selection by <paramref name="delta"/> (+1/-1), wrapping at the ends — mirrors how every other palette-style picker (VS Code, Sublime, etc.) handles arrow keys.</summary>
    public void MoveSelection(int delta)
    {
        if (FilteredCommands.Count == 0) return;
        var index = SelectedCommand is null ? -1 : FilteredCommands.IndexOf(SelectedCommand);
        index = ((index + delta) % FilteredCommands.Count + FilteredCommands.Count) % FilteredCommands.Count;
        SelectedCommand = FilteredCommands[index];
    }

    /// <summary>Marks the current <see cref="SelectedCommand"/> as confirmed; no-op if nothing's selected (e.g. the filter matched nothing).</summary>
    public void Confirm()
    {
        if (SelectedCommand is not null)
            Confirmed = true;
    }
}
