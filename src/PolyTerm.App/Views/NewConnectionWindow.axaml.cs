using Avalonia.Controls;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>New/edit connection profile dialog code-behind. Wires the view model's close and tunnels-editor delegates.</summary>
public sealed partial class NewConnectionWindow : Window
{
    public NewConnectionWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Binds <see cref="NewConnectionViewModel.RequestClose"/> to close this window, and
    /// <see cref="NewConnectionViewModel.OpenTunnelsEditorAsync"/> to launch <see cref="TunnelsWindow"/> as a
    /// modal child, returning the edited tunnel list (or <c>null</c> if cancelled).
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not NewConnectionViewModel vm) return;

        vm.RequestClose += (_, _) => Close();
        vm.OpenTunnelsEditorAsync = async defs =>
            await new TunnelsWindow { DataContext = new TunnelsViewModel(defs) }.ShowDialog<List<TunnelDefinition>?>(this);
        vm.OpenCredentialPickerAsync = async () =>
            await new CredentialPickerDialog(vm.CredentialProviders).ShowDialog<CredentialPickResult?>(this);
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();
}
