using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.Macros;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>Add/edit dialog for a single macro. Closes with the resulting <see cref="MacroDefinition"/>, or <c>null</c> if cancelled.</summary>
public sealed partial class MacroEditDialog : Window
{
    public MacroEditDialog() => InitializeComponent();

    /// <summary>Creates the dialog pre-populated for editing <paramref name="existing"/>, or blank for a new macro when <c>null</c>.</summary>
    public MacroEditDialog(MacroDefinition? existing) : this()
    {
        var vm = new MacroEditViewModel(existing);
        vm.RequestClose += (_, _) => Close(vm.Result);
        DataContext = vm;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
