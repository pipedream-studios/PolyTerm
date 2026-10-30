using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AwesomeAssertions;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers the main window's sidebar/divider/content layout (same column setup as MainWindow.axaml's
/// MainContentGrid - MainWindow itself needs the app's DI container). The sidebar used to be a fixed 220px
/// Border in an Auto column: dragging the splitter resized the column but not the Border, so dragging
/// right opened a gap between the sidebar's edge line and the splitter, and dragging left let the Border
/// overflow under the splitter and tab area.
/// </summary>
public sealed class SidebarSplitterTests
{
    private sealed record Layout(Window Window, Grid Grid, Border Sidebar, DividerSplitter Splitter, Border Content);

    private static Layout Build()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(220, GridUnitType.Pixel) { MinWidth = 160, MaxWidth = 600 });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 300 });

        var sidebar  = new Border { Background = Brushes.DarkSlateGray };
        var splitter = new DividerSplitter { Width = 5, ResizeBehavior = GridResizeBehavior.PreviousAndNext, LineBrush = Brushes.White };
        var content  = new Border { Background = Brushes.Black };
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(content, 2);
        grid.Children.Add(sidebar);
        grid.Children.Add(splitter);
        grid.Children.Add(content);

        var window = new Window { Width = 1200, Height = 800, Content = grid };
        window.Show();
        window.UpdateLayout();
        return new Layout(window, grid, sidebar, splitter, content);
    }

    private static void Drag(Layout l, double dx)
    {
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(dx, 0) });
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(dx, 0) });
        l.Window.UpdateLayout();
    }

    private static void ShouldBeFlush(Layout l)
    {
        l.Sidebar.Bounds.Right.Should().BeApproximately(l.Splitter.Bounds.Left, 0.01, "the sidebar should end exactly where the divider starts - no gap, no overlap");
        l.Splitter.Bounds.Right.Should().BeApproximately(l.Content.Bounds.Left, 0.01, "the tab area should start exactly where the divider ends");
        l.Content.Bounds.Right.Should().BeApproximately(l.Grid.Bounds.Right, 0.01);
    }

    [AvaloniaTheory]
    [InlineData(150)]
    [InlineData(-40)]
    public void DraggingTheDivider_KeepsSidebarDividerAndContentFlush(double dx)
    {
        var l = Build();

        Drag(l, dx);

        l.Sidebar.Bounds.Width.Should().BeApproximately(220 + dx, 0.01, "the sidebar should fill its column as it's resized");
        ShouldBeFlush(l);
    }

    [AvaloniaFact]
    public void DraggingPastTheLimits_ClampsTheSidebar_AndStaysFlush()
    {
        var l = Build();

        Drag(l, -500);
        l.Sidebar.Bounds.Width.Should().BeApproximately(160, 0.01, "MinWidth");
        ShouldBeFlush(l);

        Drag(l, 2000);
        l.Sidebar.Bounds.Width.Should().BeApproximately(600, 0.01, "MaxWidth");
        ShouldBeFlush(l);
    }

    [AvaloniaFact]
    public void HidingAndShowingTheSidebar_CollapsesTheColumn_AndRestoresTheDraggedWidth()
    {
        var l = Build();
        var column = new CollapsibleColumn(l.Grid.ColumnDefinitions[0], 220, 160);
        Drag(l, 80);

        column.SetVisible(false);
        l.Sidebar.IsVisible = l.Splitter.IsVisible = false;
        l.Window.UpdateLayout();
        l.Content.Bounds.Left.Should().Be(0, "the tab area should take the whole width while the tree is hidden");

        column.SetVisible(true);
        l.Sidebar.IsVisible = l.Splitter.IsVisible = true;
        l.Window.UpdateLayout();
        l.Sidebar.Bounds.Width.Should().BeApproximately(300, 0.01, "the width the user dragged to should come back, not the 220 default");
        ShouldBeFlush(l);
    }
}
