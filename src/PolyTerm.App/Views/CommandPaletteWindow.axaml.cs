using Avalonia.Controls;
using Avalonia.Input;
using PolyTerm.App.ViewModels;

namespace PolyTerm.App.Views;

/// <summary>
/// Ctrl+Shift+P command palette: a filterable list of every registered <see cref="CommandPalette.PaletteCommand"/>.
/// Closes itself on Enter (runs the selected command) or Escape (cancels) — the caller checks
/// <see cref="CommandPaletteViewModel.Confirmed"/>/<c>SelectedCommand</c> after <c>ShowDialog</c>
/// returns and runs the command itself, rather than this window invoking it directly, so any
/// exception from the command surfaces to the same place normal menu/ribbon click handlers do.
/// </summary>
public sealed partial class CommandPaletteWindow : Window
{
    public CommandPaletteWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SearchBox.Focus();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Confirm();
                break;
            case Key.Down:
                e.Handled = true;
                (DataContext as CommandPaletteViewModel)?.MoveSelection(+1);
                break;
            case Key.Up:
                e.Handled = true;
                (DataContext as CommandPaletteViewModel)?.MoveSelection(-1);
                break;
        }
    }

    private void CommandList_DoubleTapped(object? sender, TappedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (DataContext is not CommandPaletteViewModel vm) return;
        vm.Confirm();
        Close();
    }
}
