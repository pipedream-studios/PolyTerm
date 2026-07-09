using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.ViewModels;
using PolyTerm.Engine;

namespace PolyTerm.App.Views;

/// <summary>Tunnel list editor for a connection profile — add/edit/delete entries, save the list, or cancel.</summary>
public sealed partial class TunnelsWindow : Window
{
    public TunnelsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Wires <see cref="TunnelsViewModel.EditTunnelAsync"/> to open <see cref="TunnelEditDialog"/> modally.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TunnelsViewModel vm) return;

        vm.EditTunnelAsync = async existing =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return null;
            return await new TunnelEditDialog(existing).ShowDialog<TunnelDefinition?>(owner);
        };
    }

    private void TunnelEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TunnelsViewModel vm) return;
        if (TryGetContextTunnel(sender) is { } tunnel)
            _ = vm.EditAsync(tunnel);
    }

    private void TunnelDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TunnelsViewModel vm) return;
        if (TryGetContextTunnel(sender) is { } tunnel)
            vm.Delete(tunnel);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TunnelsViewModel vm)
            Close(vm.Result);
        else
            Close(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private static TunnelDefinition? TryGetContextTunnel(object? sender) =>
        sender is MenuItem { Parent: ContextMenu cm } &&
        cm.PlacementTarget?.DataContext is TunnelDefinition tunnel
            ? tunnel : null;
}
