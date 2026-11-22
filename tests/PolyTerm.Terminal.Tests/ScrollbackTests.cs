using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.Terminal.Tests;

/// <summary>Covers clearing the scrollback (terminal context menu's "Clear Scrollback").</summary>
public sealed class ScrollbackTests
{
    private static TerminalEmulator MakeEmulator() =>
        new(10, 3, scrollbackLines: 100, NullLogger<TerminalEmulator>.Instance);

    [Fact]
    public void ClearScrollback_DiscardsHistoryAndLeavesScreenAlone()
    {
        var emulator = MakeEmulator();
        for (var i = 0; i < 10; i++)
            emulator.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        emulator.ScrollbackLineCount.Should().BeGreaterThan(0);

        emulator.ClearScrollback();

        emulator.ScrollbackLineCount.Should().Be(0);
        emulator.GetScreenSnapshot()[0, 0].IsBlank.Should().BeFalse("the visible screen must survive");
    }

    [Fact]
    public void ClearScrollback_RaisesScreenUpdated_AndBufferAcceptsNewLines()
    {
        var emulator = MakeEmulator();
        for (var i = 0; i < 10; i++)
            emulator.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        var raised = false;
        emulator.ScreenUpdated += (_, _) => raised = true;

        emulator.ClearScrollback();
        raised.Should().BeTrue();

        emulator.Feed("a\r\nb\r\nc\r\nd\r\n"u8);
        emulator.ScrollbackLineCount.Should().BeGreaterThan(0);
    }

    private static string RowText(TerminalEmulator emulator, int row)
    {
        var screen = emulator.GetScreenSnapshot();
        var sb = new StringBuilder();
        for (var c = 0; c < emulator.Cols; c++)
            sb.Append(screen[row, c].IsBlank ? ' ' : char.ConvertFromUtf32(screen[row, c].Codepoint));
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void ClearScreenAndScrollback_KeepsOnlyTheCursorLine_MovedToTheTop()
    {
        var emulator = MakeEmulator();
        for (var i = 0; i < 10; i++)
            emulator.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        emulator.Feed("$ ls"u8);
        var cursorCol = emulator.CursorCol;

        emulator.ClearScreenAndScrollback();

        emulator.ScrollbackLineCount.Should().Be(0);
        RowText(emulator, 0).Should().Be("$ ls", "the prompt line stays visible without the remote side redrawing it");
        RowText(emulator, 1).Should().BeEmpty();
        RowText(emulator, 2).Should().BeEmpty();
        emulator.CursorRow.Should().Be(0);
        emulator.CursorCol.Should().Be(cursorCol, "the cursor stays where it was on its line");
    }

    [Fact]
    public void ClearScreenAndScrollback_NewOutputContinuesFromTheKeptLine()
    {
        var emulator = MakeEmulator();
        emulator.Feed("a\r\nb\r\n$ "u8);

        emulator.ClearScreenAndScrollback();
        emulator.Feed("x\r\nout"u8);

        RowText(emulator, 0).Should().Be("$ x");
        RowText(emulator, 1).Should().Be("out");
    }

    [Fact]
    public void ClearScreenAndScrollback_OnTheAlternateScreen_LeavesTheScreenAlone()
    {
        var emulator = MakeEmulator();
        for (var i = 0; i < 10; i++)
            emulator.Feed(Encoding.ASCII.GetBytes($"line{i}\r\n"));
        emulator.Feed("\x1b[?1049h\x1b[2;1Hvim"u8);

        emulator.ClearScreenAndScrollback();

        emulator.ScrollbackLineCount.Should().Be(0);
        RowText(emulator, 1).Should().Be("vim", "a full-screen program owns the alternate screen");
        emulator.CursorRow.Should().Be(1);
    }

    [Fact]
    public void ClearScreenAndScrollback_KeepsEveryRowOfAWrappedCursorLine()
    {
        var emulator = MakeEmulator();
        emulator.Feed("out1\r\nout2\r\n"u8);
        emulator.Feed("$ abcdefghijklmnop"u8); // 18 chars on a 10-wide screen: wraps onto a second row

        emulator.ClearScreenAndScrollback();

        RowText(emulator, 0).Should().Be("$ abcdefgh", "the first row of a wrapped prompt line must not be lost");
        RowText(emulator, 1).Should().Be("ijklmnop");
        RowText(emulator, 2).Should().BeEmpty();
        emulator.CursorRow.Should().Be(1);
        emulator.CursorCol.Should().Be(8);
    }

    [Fact]
    public void ClearScreenAndScrollback_KeepsTheWholeWrappedLine_WhenTheCursorIsOnItsFirstRow()
    {
        var emulator = MakeEmulator();
        emulator.Feed("out\r\n$ abcdefghijklmnop"u8);
        emulator.Feed("\x1b[2;3H"u8); // cursor back up onto the line's first row (as when editing it)

        emulator.ClearScreenAndScrollback();

        RowText(emulator, 0).Should().Be("$ abcdefgh");
        RowText(emulator, 1).Should().Be("ijklmnop");
        emulator.CursorRow.Should().Be(0);
    }

    [Fact]
    public void ClearScreenAndScrollback_DoesNotKeepTheLineAbove_WhenItEndedWithANewline()
    {
        var emulator = MakeEmulator();
        emulator.Feed("0123456789\r\n$ "u8); // exactly fills its row, then a real newline: not wrapped

        emulator.ClearScreenAndScrollback();

        RowText(emulator, 0).Should().Be("$");
        RowText(emulator, 1).Should().BeEmpty();
    }
}
