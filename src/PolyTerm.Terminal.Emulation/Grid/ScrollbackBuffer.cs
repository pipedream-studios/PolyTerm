namespace PolyTerm.Terminal;

/// <summary>
/// Ring buffer that holds scrollback lines as cell arrays.
/// Capacity is the max number of lines to keep.
/// </summary>
public sealed class ScrollbackBuffer
{
    private readonly int _capacity;
    private readonly Cell[][] _lines;
    private int _head;   // index of oldest line
    private int _count;  // how many lines are filled

    /// <summary>Creates a scrollback buffer that retains at most <paramref name="capacity"/> lines.</summary>
    public ScrollbackBuffer(int capacity)
    {
        _capacity = capacity;
        _lines = new Cell[capacity][];
    }

    /// <summary>Number of lines currently held (never exceeds capacity).</summary>
    public int Count => _count;

    /// <summary>Discards every retained line.</summary>
    public void Clear()
    {
        Array.Clear(_lines);
        _head  = 0;
        _count = 0;
    }

    /// <summary>Appends a line as the newest entry, evicting the oldest line once at capacity.</summary>
    public void Push(Cell[] line)
    {
        if (_count < _capacity)
        {
            _lines[(_head + _count) % _capacity] = line;
            _count++;
        }
        else
        {
            // Overwrite oldest
            _lines[_head] = line;
            _head = (_head + 1) % _capacity;
        }
    }

    /// <summary>
    /// Returns the line at <paramref name="index"/> from oldest (0) to newest (Count-1).
    /// </summary>
    public Cell[] this[int index] => _lines[(_head + index) % _capacity];
}
