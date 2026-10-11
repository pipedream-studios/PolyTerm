using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// The view the terminal draws: at the live bottom it must show the current screen even when scrollback
/// already holds more than a screenful (previously the window went blank after a long `cat`).
/// </summary>
public sealed class TerminalViewTests
{
    private static TerminalEmulator Filled(int lines, int cols = 20, int rows = 5)
    {
        var e = new TerminalEmulator(cols, rows, 1000, NullLogger<TerminalEmulator>.Instance);
        for (var i = 0; i < lines; i++)
            e.Feed(Encoding.ASCII.GetBytes($"line{i:000}\r\n"));
        return e;
    }

    private static string Row(Cell[][] view, int row) =>
        string.Concat(view[row].Select(c => c.IsBlank ? ' ' : (char)c.Codepoint)).TrimEnd();

    [Fact]
    public void LiveBottom_ShowsScreen_WhenScrollbackExceedsScreenHeight()
    {
        var e = Filled(100);
        e.ScrollbackLineCount.Should().BeGreaterThan(e.Rows);

        var view = TerminalSurface.BuildView(e, scrollOffset: 0);

        Row(view, 0).Should().Be("line096");
        Row(view, 3).Should().Be("line099");
    }

    [Fact]
    public void ScrolledBackPartially_ShowsScrollbackThenTopOfScreen()
    {
        var e = Filled(100);

        var view = TerminalSurface.BuildView(e, scrollOffset: 2);

        Row(view, 0).Should().Be("line094");
        Row(view, 1).Should().Be("line095");
        Row(view, 2).Should().Be("line096");
    }

    [Fact]
    public void ScrolledBackMoreThanAScreenful_ShowsOnlyScrollback()
    {
        var e = Filled(100);

        var view = TerminalSurface.BuildView(e, scrollOffset: 10);

        Row(view, 0).Should().Be("line086");
        Row(view, 4).Should().Be("line090");
    }
}
