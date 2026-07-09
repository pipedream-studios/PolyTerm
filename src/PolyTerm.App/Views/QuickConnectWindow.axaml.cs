using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>Quick-connect dialog: parses a one-line connection string and opens it directly, bypassing the vault.</summary>
public sealed partial class QuickConnectWindow : Window
{
    public QuickConnectWindow()
    {
        InitializeComponent();
        Opened += (_, _) => InputBox.Focus();
    }

    /// <summary>Binds <see cref="QuickConnectViewModel.RequestClose"/> to close this window.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is QuickConnectViewModel vm)
            vm.RequestClose += (_, _) => Close();
    }

    /// <summary>Escape closes the dialog without confirming.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
