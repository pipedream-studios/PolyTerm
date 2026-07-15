using Avalonia.Input;
using Avalonia.Media;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>
/// Covers <see cref="TerminalSurface"/>'s pure pixel-to-cell mapping, xterm-256 color palette
/// computation, and key-to-escape-sequence encoding (issue #66) — same rationale as
/// <see cref="FramebufferSurfaceTests"/>: logic with no rendering dependency, exposed
/// <c>internal static</c> and tested directly rather than left permanently uncovered.
/// </summary>
public sealed class TerminalSurfaceTests
{
    [Fact]
    public void ComputeHitTest_MapsPixelToCell_UsingRealCellSize()
    {
        // An 80x24 grid, 10x20px cells: pixel (25, 45) should land in column 2, row 2.
        var (col, row) = TerminalSurface.ComputeHitTest(25, 45, cellWidth: 10, cellHeight: 20, cols: 80, rows: 24);

        col.Should().Be(2);
        row.Should().Be(2);
    }

    [Fact]
    public void ComputeHitTest_ClampsToTheLastColumnAndRow_WhenPointerIsPastTheGrid()
    {
        var (col, row) = TerminalSurface.ComputeHitTest(10_000, 10_000, cellWidth: 10, cellHeight: 20, cols: 80, rows: 24);

        col.Should().Be(79, "clamped to the last valid column, not the raw (out-of-range) division result");
        row.Should().Be(23, "clamped to the last valid row, not the raw (out-of-range) division result");
    }

    [Fact]
    public void ComputeHitTest_ClampsToTheFirstColumnAndRow_WhenPointerIsNegative()
    {
        var (col, row) = TerminalSurface.ComputeHitTest(-50, -50, cellWidth: 10, cellHeight: 20, cols: 80, rows: 24);

        col.Should().Be(0);
        row.Should().Be(0);
    }

    [Fact]
    public void BrushForColor_Default_UsesTheDocumentedForegroundGrayOrTransparentBackground()
    {
        var fg = TerminalSurface.BrushForColor(TerminalColor.Default, isForeground: true);
        var bg = TerminalSurface.BrushForColor(TerminalColor.Default, isForeground: false);

        fg.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(0xcc, 0xcc, 0xcc));
        bg.Should().BeSameAs(Brushes.Transparent);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]         // black
    [InlineData(1, 170, 0, 0)]       // red
    [InlineData(9, 255, 85, 85)]     // bright red
    [InlineData(15, 255, 255, 255)]  // bright white
    public void BrushForColor_Named_UsesTheNamed16Palette(byte index, byte r, byte g, byte b)
    {
        var brush = TerminalSurface.BrushForColor(TerminalColor.Named(index), isForeground: true);

        brush.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(r, g, b));
    }

    [Fact]
    public void BrushForColor_Indexed_BelowSixteen_UsesTheNamed16Palette()
    {
        var brush = TerminalSurface.BrushForColor(TerminalColor.Indexed(1), isForeground: true);

        brush.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(170, 0, 0), "indices 0-15 alias the same named ANSI colors, not the 256-color cube");
    }

    [Fact]
    public void BrushForColor_Indexed_InTheColorCubeRange_UsesXTerm256()
    {
        var brush = TerminalSurface.BrushForColor(TerminalColor.Indexed(196), isForeground: true);

        brush.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(255, 0, 0), "index 196 is the standard xterm-256 pure red");
    }

    [Fact]
    public void BrushForColor_Indexed_InTheGrayscaleRange_UsesGrayscaleRamp()
    {
        var brush = TerminalSurface.BrushForColor(TerminalColor.Indexed(255), isForeground: true);

        brush.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(238, 238, 238), "index 255 is the lightest step of the xterm-256 grayscale ramp");
    }

    [Fact]
    public void BrushForColor_TrueColor_UsesTheExactRgbValue()
    {
        var brush = TerminalSurface.BrushForColor(TerminalColor.Rgb(12, 34, 56), isForeground: true);

        brush.Should().BeOfType<SolidColorBrush>().Which.Color.Should().Be(Color.FromRgb(12, 34, 56));
    }

    [Theory]
    [InlineData((byte)16, 0, 0, 0)]       // first cube entry: black
    [InlineData((byte)21, 0, 0, 255)]     // pure blue
    [InlineData((byte)196, 255, 0, 0)]    // pure red
    [InlineData((byte)231, 255, 255, 255)] // last cube entry: white
    public void XTerm256_KnownIndices_ProduceTheStandardXterm256Rgb(byte index, byte r, byte g, byte b) =>
        TerminalSurface.XTerm256(index).Color.Should().Be(Color.FromRgb(r, g, b));

    [Theory]
    [InlineData((byte)232, 8)]
    [InlineData((byte)255, 238)]
    public void GrayscaleRamp_KnownIndices_ProduceTheStandardXterm256Gray(byte index, byte expectedChannel) =>
        TerminalSurface.GrayscaleRamp(index).Color.Should().Be(Color.FromRgb(expectedChannel, expectedChannel, expectedChannel));

    [Theory]
    [InlineData(Key.Up, "\x1b[A")]
    [InlineData(Key.Down, "\x1b[B")]
    [InlineData(Key.Right, "\x1b[C")]
    [InlineData(Key.Left, "\x1b[D")]
    [InlineData(Key.Home, "\x1b[H")]
    [InlineData(Key.End, "\x1b[F")]
    [InlineData(Key.Delete, "\x1b[3~")]
    [InlineData(Key.F1, "\x1bOP")]
    [InlineData(Key.F5, "\x1b[15~")]
    [InlineData(Key.F12, "\x1b[24~")]
    [InlineData(Key.Escape, "\x1b")]
    [InlineData(Key.Tab, "\t")]
    [InlineData(Key.Return, "\r")]
    [InlineData(Key.Back, "\x7f")]
    public void KeyToBytes_MapsKnownKeysToTheirEscapeSequences(Key key, string expectedAscii)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };

        var bytes = TerminalSurface.KeyToBytes(e);

        bytes.Should().Equal(System.Text.Encoding.Latin1.GetBytes(expectedAscii));
    }

    [Fact]
    public void KeyToBytes_ReturnsNull_ForKeysWithNoTerminalMeaning()
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A };

        TerminalSurface.KeyToBytes(e).Should().BeNull("printable characters go through OnTextInput instead, not this control-key table");
    }

    [Theory]
    [InlineData(Key.Up, "\x1bOA")]
    [InlineData(Key.Down, "\x1bOB")]
    [InlineData(Key.Right, "\x1bOC")]
    [InlineData(Key.Left, "\x1bOD")]
    public void KeyToBytes_DecckmEnabled_EncodesArrowKeysAsApplicationSequences(Key key, string expectedAscii)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };

        var bytes = TerminalSurface.KeyToBytes(e, applicationCursorKeys: true);

        bytes.Should().Equal(System.Text.Encoding.Latin1.GetBytes(expectedAscii));
    }

    [Fact]
    public void KeyToBytes_DecckmEnabled_LeavesNonArrowKeysUnaffected()
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home };

        var bytes = TerminalSurface.KeyToBytes(e, applicationCursorKeys: true);

        bytes.Should().Equal(System.Text.Encoding.Latin1.GetBytes("\x1b[H"), "DECCKM only changes the four arrow keys, not Home/End/etc.");
    }
}
