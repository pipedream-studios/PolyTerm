using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>Add/edit dialog for a single SSH tunnel definition. Closes with the resulting <see cref="TunnelDefinition"/>, or <c>null</c> if cancelled.</summary>
public sealed partial class TunnelEditDialog : Window
{
    public TunnelEditDialog() => InitializeComponent();

    /// <summary>Creates the dialog pre-populated for editing <paramref name="existing"/>, or blank for a new tunnel when <c>null</c>.</summary>
    public TunnelEditDialog(TunnelDefinition? existing) : this()
    {
        var vm = new TunnelEditViewModel(existing);
        vm.RequestClose += (_, _) => Close(vm.Result);
        DataContext = vm;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
