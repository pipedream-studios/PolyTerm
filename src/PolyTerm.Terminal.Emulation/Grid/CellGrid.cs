namespace PolyTerm.Terminal;

/// <summary>
/// 2D cell grid for the terminal active screen.
/// Not thread-safe — all mutations happen on the engine thread; renderer reads a snapshot.
/// </summary>
public sealed class CellGrid
{
    private Cell[] _cells;
    private int _cols;
    private int _rows;

    /// <summary>Current column count.</summary>
    public int Cols => _cols;

    /// <summary>Current row count.</summary>
    public int Rows => _rows;

    /// <summary>Creates a grid of the given size, filled with blank cells.</summary>
    public CellGrid(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        _cells = new Cell[cols * rows];
        Fill(Cell.Empty);
    }

    /// <summary>Gets a mutable reference to the cell at the given column/row.</summary>
    public ref Cell this[int col, int row] => ref _cells[row * _cols + col];

    /// <summary>
    /// Resizes the grid in place, preserving the overlapping top-left region and
    /// filling any newly exposed cells as blank.
    /// </summary>
    public void Resize(int newCols, int newRows)
    {
        var newCells = new Cell[newCols * newRows];
        for (var i = 0; i < newCells.Length; i++)
            newCells[i] = Cell.Empty;

        var copyRows = Math.Min(_rows, newRows);
        var copyCols = Math.Min(_cols, newCols);
        for (var r = 0; r < copyRows; r++)
        for (var c = 0; c < copyCols; c++)
            newCells[r * newCols + c] = _cells[r * _cols + c];

        _cells = newCells;
        _cols  = newCols;
        _rows  = newRows;
    }

    /// <summary>Scroll up by <paramref name="lines"/>, filling vacated rows with empty cells.</summary>
    public void ScrollUp(int lines, int top, int bottom)
    {
        lines = Math.Min(lines, bottom - top + 1);
        var src = top + lines;
        var dst = top;
        var count = bottom - top + 1 - lines;
        if (count > 0)
            Array.Copy(_cells, src * _cols, _cells, dst * _cols, count * _cols);
        // Clear vacated rows
        for (var r = bottom - lines + 1; r <= bottom; r++)
            ClearRow(r);
    }

    /// <summary>Scroll down by <paramref name="lines"/>, filling vacated rows with empty cells.</summary>
    public void ScrollDown(int lines, int top, int bottom)
    {
        lines = Math.Min(lines, bottom - top + 1);
        var src = top;
        var dst = top + lines;
        var count = bottom - top + 1 - lines;
        if (count > 0)
            Array.Copy(_cells, src * _cols, _cells, dst * _cols, count * _cols);
        for (var r = top; r < top + lines; r++)
            ClearRow(r);
    }

    /// <summary>Clears an entire row to blank cells, optionally applying <paramref name="attr"/> (e.g. background color) to the blanks.</summary>
    public void ClearRow(int row, CellAttribute? attr = null)
    {
        var blank = Cell.Empty;
        if (attr.HasValue) blank.Attribute = attr.Value;
        for (var c = 0; c < _cols; c++)
            _cells[row * _cols + c] = blank;
    }

    /// <summary>Clears cells <paramref name="colStart"/>..<paramref name="colEnd"/> (inclusive) on <paramref name="row"/> to blank, optionally applying <paramref name="attr"/>.</summary>
    public void ClearRegion(int colStart, int row, int colEnd, CellAttribute? attr = null)
    {
        var blank = Cell.Empty;
        if (attr.HasValue) blank.Attribute = attr.Value;
        for (var c = colStart; c <= colEnd; c++)
            _cells[row * _cols + c] = blank;
    }

    /// <summary>Returns a defensive copy of the backing cell array, in row-major order.</summary>
    public Cell[] TakeSnapshot() => (Cell[])_cells.Clone();

    private void Fill(Cell fill)
    {
        for (var i = 0; i < _cells.Length; i++)
            _cells[i] = fill;
    }
}
