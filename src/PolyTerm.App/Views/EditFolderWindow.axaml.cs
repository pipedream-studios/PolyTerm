using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>Edit dialog for a folder: name and parent folder.</summary>
public sealed partial class EditFolderWindow : Window
{
    public EditFolderWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Binds <see cref="EditFolderViewModel.RequestClose"/> to close this window.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is EditFolderViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
