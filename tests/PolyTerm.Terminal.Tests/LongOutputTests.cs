using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Terminal;
using Xunit;

namespace PolyTerm.Terminal.Tests;

/// <summary>
/// Large dumps (cat /var/log/messages) arrive as arbitrary chunks and may contain non-ASCII text. The result
/// must not depend on where the chunk boundaries fall, and UTF-8 continuation bytes that happen to equal
/// 8-bit C1 controls (0x90 DCS, 0x9B CSI, 0x9D OSC) must stay part of the character.
/// </summary>
public sealed class LongOutputTests
{
    private static TerminalEmulator Make(int cols = 40, int rows = 6) =>
        new(cols, rows, 5000, NullLogger<TerminalEmulator>.Instance);

    private static string Dump(TerminalEmulator e)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < e.ScrollbackLineCount; i++)
            sb.AppendLine(Text(e.GetScrollbackLine(i)));
        var snap = e.GetScreenSnapshot();
        for (var r = 0; r < e.Rows; r++)
            sb.AppendLine(Text(Enumerable.Range(0, e.Cols).Select(c => snap[r, c]).ToArray()));
        return sb.ToString();
    }

    private static string Text(Cell[] row) =>
        string.Concat(row.Where(c => !c.IsContinuation).Select(c => c.IsBlank ? " " : char.ConvertFromUtf32(c.Codepoint))).TrimEnd();

    [Theory]
    [InlineData("║ text")]      // E2 95 91
    [InlineData("╛ x")]         // E2 95 9B - continuation byte is 0x9B (8-bit CSI)
    [InlineData("▐ x")]         // E2 96 90 - continuation byte is 0x90 (8-bit DCS)
    [InlineData("═ x")]         // E2 95 90
    [InlineData("” quoted")]    // E2 80 9D - continuation byte is 0x9D (8-bit OSC)
    public void Utf8ContinuationBytesThatLookLikeC1Controls_ArePrinted(string text)
    {
        var e = Make();
        e.Feed(Encoding.UTF8.GetBytes(text + "\r\nnext line\r\n"));

        Dump(e).Should().Contain(text).And.Contain("next line");
    }

    [Fact]
    public void Output_IsIndependentOfChunkBoundaries()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 400; i++)
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Oct 11 16:{i % 60:00}:01 host sshd[{i}]: ‘session’ ╛ opened ” for user root ║ {new string('x', i % 70)}\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        var whole = Make();
        whole.Feed(bytes);

        foreach (var chunk in new[] { 1, 3, 7, 64, 1021 })
        {
            var split = Make();
            for (var i = 0; i < bytes.Length; i += chunk)
                split.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
            Dump(split).Should().Be(Dump(whole), $"chunk size {chunk} must not change the result");
        }
    }

    [Fact]
    public void LongUtf8Dump_KeepsEveryLineIntact()
    {
        var lines = Enumerable.Range(0, 300).Select(i => $"line {i:000} ╛ ok ” end").ToArray();
        var e = Make(cols: 60);
        e.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"));

        var dump = Dump(e).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        dump.Should().ContainInOrder(lines);
    }
}
