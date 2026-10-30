using System.Text;
using System.Text.RegularExpressions;

namespace PolyTerm.App.McpBridge;

/// <summary>
/// Thread-safe rolling buffer of decoded, ANSI-stripped terminal output lines.
/// Caps at <see cref="MaxLines"/> (5000) lines, dropping the oldest as new ones arrive.
/// Written by the engine thread via Append(); read by the MCP bridge pipe thread.
/// </summary>
internal sealed partial class TerminalOutputBuffer
{
    private readonly StringBuilder _partial = new();
    private readonly Queue<string>  _lines   = new();
    private const    int            MaxLines = 5000;
    private          long           _totalLinesWritten;
    private          long           _lastOutputTicks;

    public long          TotalLinesWritten => Volatile.Read(ref _totalLinesWritten);
    public DateTimeOffset LastOutputAt      => new DateTimeOffset(Volatile.Read(ref _lastOutputTicks), TimeSpan.Zero);

    /// <summary>Decodes and accumulates raw output bytes, committing a line each time a <c>\n</c> is seen.</summary>
    public void Append(ReadOnlyMemory<byte> data)
    {
        Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

        // Best-effort UTF-8; replacement chars for invalid sequences are acceptable
        var text = Encoding.UTF8.GetString(data.Span);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                Commit(_partial.ToString());
                _partial.Clear();
            }
            // ESC and BEL are kept until Commit so StripAnsi can see whole escape sequences - dropping
            // ESC here left their parameter text behind ("[?2004h", "]0;user@host: ~").
            else if (ch >= ' ' || ch == '\t' || ch == '\x1B' || ch == '\x07')
            {
                _partial.Append(ch);

                // Windows ConPTY (conhost) often moves to the next line with a cursor-position sequence
                // (ESC[8;1H) instead of "\r\n", so without this a command's output after its first line
                // never gets committed. Only commits when the line has visible text, so a full-screen
                // repaint doesn't turn into a run of blank lines.
                if (ch is 'H' or 'f' or 'd' or 'E' or 'F' && EndsWithRowMovingCsi())
                {
                    var line = Clean(_partial.ToString());
                    if (line.Trim().Length > 0)
                    {
                        Enqueue(line);
                        _partial.Clear();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether <see cref="_partial"/> ends with a CSI sequence that can move the cursor to a different row:
    /// CUP/HVP (<c>H</c>/<c>f</c>), VPA (<c>d</c>), CNL/CPL (<c>E</c>/<c>F</c>). Parameters are digits and <c>;</c>
    /// only, so private-mode sequences like <c>ESC[?25h</c> never match.
    /// </summary>
    private bool EndsWithRowMovingCsi()
    {
        var i = _partial.Length - 2;
        while (i >= 0 && (char.IsAsciiDigit(_partial[i]) || _partial[i] == ';')) i--;
        return i >= 1 && _partial[i] == '[' && _partial[i - 1] == '\x1B';
    }

    /// <summary>Cleans a completed line and enqueues it.</summary>
    private void Commit(string raw) => Enqueue(Clean(raw));

    /// <summary>Strips ANSI escape sequences, then any stray ESC/BEL left over.</summary>
    private static string Clean(string raw) => StripAnsi(raw).Replace("\x1B", "").Replace("\x07", "");

    /// <summary>Enqueues an already-cleaned line, evicting the oldest line if at capacity.</summary>
    private void Enqueue(string line)
    {
        lock (_lines)
        {
            if (_lines.Count >= MaxLines) _lines.Dequeue();
            _lines.Enqueue(line);
            Interlocked.Increment(ref _totalLinesWritten);
        }
    }

    /// <summary>Returns the last <paramref name="count"/> committed lines.</summary>
    public string[] GetRecentLines(int count)
    {
        lock (_lines)
        {
            var skip = Math.Max(0, _lines.Count - count);
            return _lines.Skip(skip).ToArray();
        }
    }

    /// <summary>
    /// Returns lines committed after <paramref name="lineIndex"/> (exclusive).
    /// <paramref name="lineIndex"/> should be a snapshot of <see cref="TotalLinesWritten"/>
    /// taken before the command was sent.
    /// </summary>
    public string[] GetLinesSince(long lineIndex)
    {
        lock (_lines)
        {
            var bufStart = _totalLinesWritten - _lines.Count;
            var skip     = (int)Math.Max(0, lineIndex - bufStart);
            return skip >= _lines.Count ? [] : _lines.Skip(skip).ToArray();
        }
    }

    // In order: OSC/DCS/SOS/PM/APC strings (ESC ] 0;title BEL, terminated by BEL or ST, or running to
    // end of line if unterminated), CSI sequences, charset designation (ESC ( B), then any other
    // two-byte escape.
    [GeneratedRegex(@"\x1B[\]PX^_][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B\[[0-?]*[ -/]*[@-~]|\x1B[()*+][ -~]|\x1B[@-Z\\-_]")]
    private static partial Regex AnsiPattern();

    private static string StripAnsi(string s) => AnsiPattern().Replace(s, "");
}
