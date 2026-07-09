using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>Rename dialog for a connection or folder profile.</summary>
public sealed partial class RenameWindow : Window
{
    public RenameWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Binds <see cref="RenameViewModel.RequestClose"/> to close this window.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is RenameViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
