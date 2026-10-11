using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.Terminal.Tests;

/// <summary>
/// Long lines must wrap onto the next row (deferred/"pending" wrap, as xterm does). Previously the cursor
/// stuck in the last column, so every character past the width overwrote it - which scrambled long
/// commands and the cursor math shells use to redraw them.
/// </summary>
public sealed class AutowrapTests
{
    private static TerminalEmulator Make(int cols = 10, int rows = 4) =>
        new(cols, rows, 100, NullLogger<TerminalEmulator>.Instance);

    private static string Row(TerminalEmulator e, int row, int count = 10)
    {
        var snap = e.GetScreenSnapshot();
        return string.Concat(Enumerable.Range(0, count).Select(c => snap[row, c].IsBlank ? ' ' : (char)snap[row, c].Codepoint)).TrimEnd();
    }

    [Fact]
    public void LongLine_WrapsOntoNextRow()
    {
        var e = Make();
        e.Feed("0123456789ABC"u8);

        Row(e, 0).Should().Be("0123456789");
        Row(e, 1).Should().Be("ABC");
        e.CursorRow.Should().Be(1);
        e.CursorCol.Should().Be(3);
    }

    [Fact]
    public void ExactlyFullLine_DefersTheWrapUntilTheNextCharacter()
    {
        var e = Make();
        e.Feed("0123456789"u8);

        e.CursorRow.Should().Be(0, "the wrap is pending, not taken yet");
        e.CursorCol.Should().Be(9);

        e.Feed("X"u8);
        e.CursorRow.Should().Be(1);
        Row(e, 1).Should().Be("X");
    }

    [Fact]
    public void ExactlyFullLine_ThenNewline_DoesNotLeaveABlankRow()
    {
        var e = Make();
        e.Feed("0123456789\r\nnext"u8);

        Row(e, 0).Should().Be("0123456789");
        Row(e, 1).Should().Be("next");
    }

    [Fact]
    public void CarriageReturnAtPendingWrap_OverwritesTheSameRow()
    {
        var e = Make();
        e.Feed("0123456789\rZ"u8);

        Row(e, 0).Should().Be("Z123456789");
        e.CursorRow.Should().Be(0);
    }

    [Fact]
    public void CursorMovementCancelsPendingWrap()
    {
        var e = Make();
        e.Feed("0123456789\u001b[1DZ"u8); // CUB 1 then print: overwrite column 8, no wrap

        Row(e, 0).Should().Be("01234567Z9");
        e.CursorRow.Should().Be(0);
    }

    [Fact]
    public void AutowrapDisabled_OverwritesLastColumn()
    {
        var e = Make();
        e.Feed("\u001b[?7l0123456789ABC"u8);

        Row(e, 0).Should().Be("012345678C");
        e.CursorRow.Should().Be(0);
    }

    [Fact]
    public void SgrDoesNotCancelPendingWrap()
    {
        var e = Make();
        e.Feed("0123456789\u001b[31mX"u8);

        Row(e, 0).Should().Be("0123456789");
        Row(e, 1).Should().Be("X");
    }
}
