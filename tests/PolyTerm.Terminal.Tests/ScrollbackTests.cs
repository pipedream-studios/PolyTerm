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
}
