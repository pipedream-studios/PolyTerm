using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>The main window's dismissable connection error banner, with selectable text and a Copy button.</summary>
public partial class ConnectionErrorBanner : UserControl
{
    public ConnectionErrorBanner() => InitializeComponent();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await ErrorClipboard.CopyAsync(this, vm, vm.ConnectionError);
    }
}
