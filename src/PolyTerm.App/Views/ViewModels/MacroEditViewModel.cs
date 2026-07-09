using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.App.Macros;

namespace PolyTerm.App.ViewModels;

/// <summary>Backs the single-macro add/edit dialog.</summary>
public sealed partial class MacroEditViewModel : ViewModelBase
{
    private readonly Guid _id;
    private readonly bool _isEditing;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _body = "";

    [ObservableProperty] private bool _appendEnter = true;

    public string WindowTitle => _isEditing ? "Edit Macro" : "Add Macro";

    /// <summary>The macro produced by <see cref="Save"/>; null until the user confirms.</summary>
    public MacroDefinition? Result { get; private set; }
    public event EventHandler? RequestClose;

    /// <param name="existing">Pass an existing macro to open in edit mode; null creates a new one.</param>
    public MacroEditViewModel(MacroDefinition? existing)
    {
        _isEditing = existing is not null;
        _id = existing?.Id ?? Guid.NewGuid();

        if (existing is not null)
        {
            _name        = existing.Name;
            _body        = existing.Body;
            _appendEnter = existing.AppendEnter;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = new MacroDefinition
        {
            Id          = _id,
            Name        = Name.Trim(),
            Body        = Body,
            AppendEnter = AppendEnter,
        };
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Body);
}
