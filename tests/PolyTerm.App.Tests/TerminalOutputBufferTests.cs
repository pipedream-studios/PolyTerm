using System.Text;
using AwesomeAssertions;
using PolyTerm.App.McpBridge;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="TerminalOutputBuffer"/>'s escape-sequence stripping - the text MCP clients get back
/// from terminal_execute/terminal_read. ESC used to be dropped as a control character before the
/// stripping regex ran, so sequences were never matched and their parameter text leaked through.
/// </summary>
public sealed class TerminalOutputBufferTests
{
    private static string[] Feed(params string[] chunks)
    {
        var buffer = new TerminalOutputBuffer();
        foreach (var chunk in chunks)
            buffer.Append(Encoding.UTF8.GetBytes(chunk));
        return buffer.GetRecentLines(100);
    }

    [Fact]
    public void BashPrompt_WithBracketedPasteTitleAndColours_IsStrippedToPlainText()
    {
        // Captured from a real Debian bash session over SSH (the default PS1 with colour + xterm title).
        var lines = Feed("\x1B[?2004h\x1B]0;rob@destiny: ~\x07\x1B[01;32mrob@destiny\x1B[00m:\x1B[01;34m~\x1B[00m$ hostname\r\n\x1B[?2004ldestiny\r\n");

        lines.Should().Equal("rob@destiny:~$ hostname", "destiny");
    }

    [Fact]
    public void OscTerminatedByStringTerminator_IsStripped()
    {
        Feed("\x1B]2;some title\x1B\\after\n").Should().Equal("after");
    }

    [Fact]
    public void UnterminatedOsc_IsStrippedToEndOfLine()
    {
        Feed("before\x1B]0;never terminated\n").Should().Equal("before");
    }

    [Fact]
    public void CharsetDesignation_IsStripped()
    {
        // tput sgr0 on many terminfo entries emits ESC ( B alongside the SGR reset.
        Feed("\x1B[1mbold\x1B(B\x1B[m plain\n").Should().Equal("bold plain");
    }

    [Fact]
    public void EscapeSequenceSplitAcrossAppendCalls_IsStillStripped()
    {
        Feed("x\x1B[01;", "32mgreen\x1B[0m\n").Should().Equal("xgreen");
    }

    [Fact]
    public void WindowsConhostOutput_PositionedWithCursorMoves_IsSplitIntoLines()
    {
        // Captured from Windows 11 OpenSSH (cmd under ConPTY) running "hostname & whoami & ver": after the
        // first "\r\n", conhost moves to later rows with ESC[row;colH instead of emitting newlines.
        var lines = Feed(
            "hostname & whoami & ver\r\nTengen\r\n\x1B]0;Administrator: C:\\WINDOWS\\system32\\conhost.exe - whoami  \x07\x1B[?25ltengen\\ulgri" +
            "\x1B[8;1HMicrosoft Windows [Version 10.0.26200.9711]\x1B[10;1Hulgri@TENGEN C:\\Users\\ulgri>\n");

        lines.Should().Equal(
            "hostname & whoami & ver",
            "Tengen",
            "tengen\\ulgri",
            "Microsoft Windows [Version 10.0.26200.9711]",
            "ulgri@TENGEN C:\\Users\\ulgri>");
    }

    [Fact]
    public void CursorMovesWithNoPendingText_DoNotProduceBlankLines()
    {
        Feed("\x1B[H\x1B[2;1H\x1B[3;1Htext\n").Should().Equal("text");
    }

    [Fact]
    public void PrivateModeSequencesEndingInH_AreNotTreatedAsRowMoves()
    {
        // ESC[?1049h etc. end in lowercase h, but make sure nothing private-mode commits mid-line either.
        Feed("a\x1B[?1049hb\x1B[?25hc\n").Should().Equal("abc");
    }

    [Fact]
    public void OtherControlCharacters_AreStillDropped_ButTabsKept()
    {
        Feed("a\tb\u0008c\u0000d\n").Should().Equal("a\tbcd");
    }
}
