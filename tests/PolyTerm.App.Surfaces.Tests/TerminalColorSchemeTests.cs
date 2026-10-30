using Avalonia.Media;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.App.Surfaces.Tests;

/// <summary>Covers the built-in <see cref="TerminalColorScheme"/>s and how <see cref="TerminalSurface"/> maps cell colours through one.</summary>
public sealed class TerminalColorSchemeTests
{
    [Fact]
    public void EveryBuiltInScheme_HasSixteenAnsiColours_AndAUniqueName()
    {
        TerminalColorScheme.BuiltIn.Should().AllSatisfy(s => s.Ansi.Should().HaveCount(16, s.Name));
        TerminalColorScheme.BuiltIn.Select(s => s.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Default_IsTheFirstBuiltIn_AndNamedAsDocumented()
    {
        TerminalColorScheme.Default.Should().BeSameAs(TerminalColorScheme.BuiltIn[0]);
        TerminalColorScheme.Default.Name.Should().Be(TerminalColorScheme.DefaultName);
    }

    [Theory]
    [InlineData("dracula", "Dracula")]
    [InlineData("SOLARIZED LIGHT", "Solarized Light")]
    [InlineData(null, TerminalColorScheme.DefaultName)]
    [InlineData("No Such Scheme", TerminalColorScheme.DefaultName)]
    public void Find_IsCaseInsensitive_AndFallsBackToTheDefault(string? name, string expected)
    {
        TerminalColorScheme.Find(name).Name.Should().Be(expected);
    }

    [Fact]
    public void Default_HasAReadableBlue_UnlikeTheClassicPalette()
    {
        // The reason for the new default: classic ANSI blue (0,0,170) on #1E1E1E is nearly invisible, which is
        // what bash's directory colour and many prompts use.
        static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        Luminance(TerminalColorScheme.Default.Ansi[4]).Should().BeGreaterThan(Luminance(TerminalColorScheme.Classic.Ansi[4]) * 5);
    }

    [Fact]
    public void BrushForColor_WithAScheme_UsesItsForegroundAndAnsiColours()
    {
        var scheme = TerminalColorScheme.Find("Dracula");

        ((ISolidColorBrush)TerminalSurface.BrushForColor(TerminalColor.Default, true, scheme)).Color.Should().Be(scheme.Foreground);
        ((ISolidColorBrush)TerminalSurface.BrushForColor(TerminalColor.Named(4), true, scheme)).Color.Should().Be(scheme.Ansi[4]);
        ((ISolidColorBrush)TerminalSurface.BrushForColor(TerminalColor.Indexed(12), true, scheme)).Color.Should().Be(scheme.Ansi[12]);
        TerminalSurface.BrushForColor(TerminalColor.Default, false, scheme).Should().BeSameAs(Brushes.Transparent,
            "the scheme background is painted once for the whole surface");
    }

    [Fact]
    public void BrushForColor_256ColourCube_IsTheSameInEveryScheme()
    {
        var a = (ISolidColorBrush)TerminalSurface.BrushForColor(TerminalColor.Indexed(196), true, TerminalColorScheme.Find("Nord"));
        var b = (ISolidColorBrush)TerminalSurface.BrushForColor(TerminalColor.Indexed(196), true, TerminalColorScheme.Find("Tango Light"));

        a.Color.Should().Be(b.Color);
    }
}
