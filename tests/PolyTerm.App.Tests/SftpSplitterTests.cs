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
/// Covers a session tab's surface/divider/SFTP panel layout (<see cref="SessionContentGrid"/>, as used by
/// MainWindow.axaml's tab content template). The panel used to be a fixed 250px Border in an Auto row, so
/// dragging the divider resized the row but not the panel - the same bug the sidebar had.
/// </summary>
public sealed class SftpSplitterTests
{
    private sealed record Layout(Window Window, SessionContentGrid Grid, Border Surface, DividerSplitter Splitter, Border Panel);

    private static Layout Build(bool open = true)
    {
        var grid     = new SessionContentGrid { SftpPanelOpen = open };
        var surface  = new Border { Background = Brushes.Black };
        var splitter = new DividerSplitter { Height = 5, ResizeBehavior = GridResizeBehavior.PreviousAndNext, LineBrush = Brushes.White, IsVisible = open };
        var panel    = new Border { Background = Brushes.DarkSlateGray, IsVisible = open };
        Grid.SetRow(surface, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(panel, 2);
        grid.Children.Add(surface);
        grid.Children.Add(splitter);
        grid.Children.Add(panel);

        var window = new Window { Width = 1200, Height = 800, Content = grid };
        window.Show();
        window.UpdateLayout();
        return new Layout(window, grid, surface, splitter, panel);
    }

    private static void SetOpen(Layout l, bool open)
    {
        l.Grid.SftpPanelOpen = open;
        l.Splitter.IsVisible = l.Panel.IsVisible = open;
        l.Window.UpdateLayout();
    }

    private static void Drag(Layout l, double dy)
    {
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = default });
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, dy) });
        l.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(0, dy) });
        l.Window.UpdateLayout();
    }

    private static void ShouldBeFlush(Layout l)
    {
        l.Surface.Bounds.Bottom.Should().BeApproximately(l.Splitter.Bounds.Top, 0.01, "the surface should end exactly where the divider starts");
        l.Splitter.Bounds.Bottom.Should().BeApproximately(l.Panel.Bounds.Top, 0.01, "the SFTP panel should start exactly where the divider ends");
        l.Panel.Bounds.Bottom.Should().BeApproximately(l.Grid.Bounds.Bottom, 0.01);
    }

    [AvaloniaTheory]
    [InlineData(-150)]
    [InlineData(60)]
    public void DraggingTheDivider_ResizesThePanel_AndKeepsEverythingFlush(double dy)
    {
        var l = Build();

        Drag(l, dy);

        l.Panel.Bounds.Height.Should().BeApproximately(SessionContentGrid.DefaultSftpHeight - dy, 0.01,
            "the panel should fill its row as it's resized (dragging up makes it taller)");
        ShouldBeFlush(l);
    }

    [AvaloniaFact]
    public void DraggingPastTheLimits_KeepsThePanelAndSurfaceAtTheirMinimums()
    {
        var l = Build();

        Drag(l, 500);
        l.Panel.Bounds.Height.Should().BeApproximately(SessionContentGrid.MinSftpHeight, 0.01);
        ShouldBeFlush(l);

        Drag(l, -2000);
        l.Surface.Bounds.Height.Should().BeApproximately(SessionContentGrid.MinSurfaceHeight, 0.01);
        ShouldBeFlush(l);
    }

    [AvaloniaFact]
    public void ClosedPanel_GivesTheSurfaceTheWholeTab_AndReopeningRestoresTheDraggedHeight()
    {
        var l = Build(open: false);
        l.Surface.Bounds.Height.Should().Be(l.Grid.Bounds.Height);

        SetOpen(l, true);
        Drag(l, -100);
        SetOpen(l, false);
        l.Surface.Bounds.Height.Should().Be(l.Grid.Bounds.Height);

        SetOpen(l, true);
        l.Panel.Bounds.Height.Should().BeApproximately(SessionContentGrid.DefaultSftpHeight + 100, 0.01);
        ShouldBeFlush(l);
    }
}
