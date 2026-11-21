using Avalonia.Controls;

namespace PolyTerm.App.Views;

/// <summary>
/// Shows/hides a resizable grid row (a session tab's SFTP panel), remembering the height the user dragged it
/// to. The row counterpart of <see cref="CollapsibleColumn"/>, for the same reason: an Auto row holding a
/// fixed-height child left the child at its own height while the splitter resized the row around it.
/// </summary>
internal sealed class CollapsibleRow(RowDefinition row, double defaultHeight, double minHeight)
{
    private double _restoreHeight = defaultHeight;

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            row.MinHeight = minHeight;
            row.Height    = new GridLength(_restoreHeight);
        }
        else
        {
            if (row.Height is { IsAbsolute: true, Value: > 0 } current)
                _restoreHeight = current.Value;
            row.MinHeight = 0;
            row.Height    = new GridLength(0);
        }
    }
}
