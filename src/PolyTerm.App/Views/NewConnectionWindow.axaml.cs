using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>New/edit connection profile dialog code-behind. Wires the view model's close, tunnels-editor, credential-picker and folder-picker delegates.</summary>
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
            await new CredentialPickerDialog(vm.CredentialProviders, includeSshKeys: vm.IsSshSelected).ShowDialog<CredentialPickResult?>(this);
        vm.PickFolderAsync = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder to share",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();
}
