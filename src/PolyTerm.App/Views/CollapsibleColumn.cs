using Avalonia.Controls;

namespace PolyTerm.App.Views;

/// <summary>
/// Shows/hides a resizable grid column (the connection tree sidebar), remembering the width the user
/// dragged it to. The column has a real pixel width so its content can fill it exactly as the splitter
/// moves - an Auto column holding a fixed-width child (the old approach, used so hiding collapsed it)
/// left the child at its own width while the splitter resized the column around it.
/// </summary>
internal sealed class CollapsibleColumn(ColumnDefinition column, double defaultWidth, double minWidth)
{
    private double _restoreWidth = defaultWidth;

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            column.MinWidth = minWidth;
            column.Width    = new GridLength(_restoreWidth);
        }
        else
        {
            if (column.Width is { IsAbsolute: true, Value: > 0 } current)
                _restoreWidth = current.Value;
            column.MinWidth = 0;
            column.Width    = new GridLength(0);
        }
    }
}
