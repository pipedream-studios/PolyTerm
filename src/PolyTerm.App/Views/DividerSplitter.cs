using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PolyTerm.App.Views;

/// <summary>
/// A <see cref="GridSplitter"/> that is its own divider line: a transparent grab strip with a 1px
/// <see cref="LineBrush"/> line drawn down (or across) its exact centre. The visible line used to be a
/// neighbouring Border's edge, next to a separate grey splitter strip (Fluent's GridSplitter has a 6px
/// MinWidth and its own background), so the line and the place to grab it never stayed aligned.
/// </summary>
public sealed class DividerSplitter : GridSplitter
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<DividerSplitter, IBrush?>(nameof(LineBrush));

    static DividerSplitter() => AffectsRender<DividerSplitter>(LineBrushProperty);

    public DividerSplitter()
    {
        // Local values, so they win over the Fluent theme's GridSplitter setters (kept for everything else).
        Background = Brushes.Transparent;
        MinWidth   = 0;
        MinHeight  = 0;
    }

    /// <summary>Colour of the 1px divider line.</summary>
    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(GridSplitter);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (LineBrush is null) return;

        var bounds = Bounds;
        // Auto follows GridSplitter's own rule for a stretched splitter: taller than wide resizes columns.
        var resizesColumns = ResizeDirection switch
        {
            GridResizeDirection.Columns => true,
            GridResizeDirection.Rows    => false,
            _                           => bounds.Width <= bounds.Height,
        };
        var line = resizesColumns
            ? new Rect(Math.Floor(bounds.Width / 2), 0, 1, bounds.Height)
            : new Rect(0, Math.Floor(bounds.Height / 2), bounds.Width, 1);
        context.FillRectangle(LineBrush, line);
    }
}
