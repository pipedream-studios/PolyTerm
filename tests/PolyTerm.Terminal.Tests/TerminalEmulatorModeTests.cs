using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.Terminal.Tests;

/// <summary>
/// Covers DECCKM, IRM, and LNM (issue #75) — previously silent no-ops in <see cref="TerminalEmulator.ApplyMode"/>.
/// </summary>
public sealed class TerminalEmulatorModeTests
{
    private static TerminalEmulator MakeEmulator(int cols = 10, int rows = 5) =>
        new(cols, rows, scrollbackLines: 100, NullLogger<TerminalEmulator>.Instance);

    // ─── DECCKM (private mode 1) ───────────────────────────────────────────────

    [Fact]
    public void CursorKeyApplicationMode_DefaultsToFalse()
    {
        var emulator = MakeEmulator();
        emulator.CursorKeyApplicationMode.Should().BeFalse();
    }

    [Fact]
    public void CursorKeyApplicationMode_SetByDeccm_ClearedByReset()
    {
        var emulator = MakeEmulator();

        emulator.Feed("\x1b[?1h"u8);
        emulator.CursorKeyApplicationMode.Should().BeTrue("CSI ?1h should enable DECCKM");

        emulator.Feed("\x1b[?1l"u8);
        emulator.CursorKeyApplicationMode.Should().BeFalse("CSI ?1l should disable DECCKM");
    }

    [Fact]
    public void CursorKeyApplicationMode_ResetByRis()
    {
        var emulator = MakeEmulator();
        emulator.Feed("\x1b[?1h"u8);

        emulator.Feed([0x1B, (byte)'c']); // ESC c = RIS full reset

        emulator.CursorKeyApplicationMode.Should().BeFalse();
    }

    // ─── IRM (mode 4) ───────────────────────────────────────────────────────────

    [Fact]
    public void InsertMode_Off_PrintOverwritesInPlace()
    {
        var emulator = MakeEmulator();
        emulator.Feed("ABCDE"u8);
        emulator.Feed("\x1b[H"u8); // CUP home
        emulator.Feed("X"u8);

        RowText(emulator, 0, 5).Should().Be("XBCDE");
    }

    [Fact]
    public void InsertMode_On_PrintShiftsExistingCellsRight_DroppingOverflow()
    {
        var emulator = MakeEmulator();
        emulator.Feed("ABCDE"u8);
        emulator.Feed("\x1b[4h"u8);  // IRM on (non-private mode 4)
        emulator.Feed("\x1b[H"u8);   // CUP home
        emulator.Feed("X"u8);

        RowText(emulator, 0, 5).Should().Be("XABCD", "the trailing E should be pushed off the end of the row");
    }

    [Fact]
    public void InsertMode_TurnedOffAgain_PrintOverwritesAsNormal()
    {
        var emulator = MakeEmulator();
        emulator.Feed("ABCDE"u8);
        emulator.Feed("\x1b[4h"u8);
        emulator.Feed("\x1b[4l"u8); // IRM off
        emulator.Feed("\x1b[H"u8);
        emulator.Feed("X"u8);

        RowText(emulator, 0, 5).Should().Be("XBCDE");
    }

    // ─── LNM (mode 20) ──────────────────────────────────────────────────────────

    [Fact]
    public void LineFeedMode_Off_LfDoesNotMoveCursorToColumnZero()
    {
        var emulator = MakeEmulator();
        emulator.Feed("AB"u8); // cursor now at col 2

        emulator.Feed("\n"u8);

        emulator.CursorCol.Should().Be(2, "LNM is off by default, so a bare LF shouldn't imply a carriage return");
        emulator.CursorRow.Should().Be(1);
    }

    [Fact]
    public void LineFeedMode_On_LfAlsoPerformsCarriageReturn()
    {
        var emulator = MakeEmulator();
        emulator.Feed("\x1b[20h"u8); // LNM on (non-private mode 20)
        emulator.Feed("AB"u8);       // cursor now at col 2

        emulator.Feed("\n"u8);

        emulator.CursorCol.Should().Be(0, "LNM should make LF also perform a carriage return");
        emulator.CursorRow.Should().Be(1);
    }

    [Fact]
    public void LineFeedMode_On_AlsoAppliesToIndEscapeSequence()
    {
        var emulator = MakeEmulator();
        emulator.Feed("\x1b[20h"u8);
        emulator.Feed("AB"u8);

        emulator.Feed([0x1B, (byte)'D']); // ESC D = IND

        emulator.CursorCol.Should().Be(0);
        emulator.CursorRow.Should().Be(1);
    }

    // ─── Private-mode prefix regression (found while implementing DECCKM) ────────

    /// <summary>
    /// Alt-screen switching (CSI ?1049h/l, used by vim/htop/less on entry and exit) depends on the same
    /// '?' private-mode prefix as DECCKM. Before the VtParser fix alongside this, ApplyMode's isPrivate
    /// check could never be true, so this never worked at the TerminalEmulator level either - not just
    /// DECCKM. Regression coverage for that, not itself part of issue #75's three named modes.
    /// </summary>
    [Fact]
    public void PrivateModePrefix_AltScreenSwitch_RestoresOriginalContentOnExit()
    {
        var emulator = MakeEmulator();
        emulator.Feed("NORMAL"u8);

        emulator.Feed("\x1b[?1049h"u8); // enter alt screen
        RowText(emulator, 0, 6).Should().NotBe("NORMAL", "the alternate screen starts blank");
        emulator.Feed("ALTSCR"u8);

        emulator.Feed("\x1b[?1049l"u8); // exit alt screen
        RowText(emulator, 0, 6).Should().Be("NORMAL", "leaving the alt screen should restore the original buffer's content");
    }

    private static string RowText(TerminalEmulator emulator, int row, int cols)
    {
        var snap = emulator.GetScreenSnapshot();
        var chars = new char[cols];
        for (var c = 0; c < cols; c++)
            chars[c] = (char)snap[row, c].Codepoint;
        return new string(chars);
    }
}
