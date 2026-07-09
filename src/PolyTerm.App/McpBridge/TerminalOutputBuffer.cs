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

    /// <summary>Decodes and accumulates raw output bytes, committing a line each time a <c>\n</c> is seen; control characters other than tab are dropped.</summary>
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
            else if (ch >= ' ' || ch == '\t')
            {
                _partial.Append(ch);
            }
        }
    }

    /// <summary>Strips ANSI escape sequences from a completed line and enqueues it, evicting the oldest line if at capacity.</summary>
    private void Commit(string raw)
    {
        var line = StripAnsi(raw);
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

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiPattern();

    private static string StripAnsi(string s) => AnsiPattern().Replace(s, "");
}
