using Avalonia.Controls;
using Avalonia.Interactivity;
using PolyTerm.App.Macros;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// Non-modal macro list/run window — stays open while the user switches tabs in the main window
/// so a macro can be run repeatedly against whichever session is currently active.
/// </summary>
public sealed partial class MacrosWindow : Window
{
    public MacrosWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Wires <see cref="MacrosViewModel.EditMacroAsync"/> to open <see cref="MacroEditDialog"/> modally.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MacrosViewModel vm) return;

        vm.EditMacroAsync = async existing =>
            await new MacroEditDialog(existing).ShowDialog<MacroDefinition?>(this);
    }

    private void MacroEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosViewModel vm) return;
        if (TryGetContextMacro(sender) is { } macro)
            _ = vm.EditAsync(macro);
    }

    private void MacroDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MacrosViewModel vm) return;
        if (TryGetContextMacro(sender) is { } macro)
            vm.Delete(macro);
    }

    private void MacroRun_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosViewModel vm && (sender as Button)?.DataContext is MacroDefinition macro)
            _ = vm.RunAsync(macro);
    }

    private void MacroBroadcast_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MacrosViewModel vm && (sender as Button)?.DataContext is MacroDefinition macro)
            _ = vm.BroadcastMacroAsync(macro);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private static MacroDefinition? TryGetContextMacro(object? sender) =>
        sender is MenuItem { Parent: ContextMenu cm } &&
        cm.PlacementTarget?.DataContext is MacroDefinition macro
            ? macro : null;
}
