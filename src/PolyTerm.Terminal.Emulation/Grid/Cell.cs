namespace PolyTerm.Terminal;

/// <summary>
/// A single cell in the terminal grid.
/// Wide characters (CJK) occupy two columns; the right half is a continuation cell (IsWide=false, IsContinuation=true).
/// </summary>
public struct Cell
{
    /// <summary>The codepoint stored in this cell (0 = empty/space).</summary>
    public int Codepoint;

    /// <summary>SGR visual attributes (colors, bold, underline, etc.) applied to this cell.</summary>
    public CellAttribute Attribute;

    /// <summary>True when this cell holds the left half of a double-width (wide) character.</summary>
    public bool IsWide;       // occupies two columns

    /// <summary>True when this cell is the right-half placeholder for a preceding wide character.</summary>
    public bool IsContinuation; // right half of a wide char

    /// <summary>A blank cell with default attributes, used to initialize and clear the grid.</summary>
    public static Cell Empty => new() { Codepoint = ' ', Attribute = CellAttribute.Default };

    /// <summary>True when the cell has no visible content (empty codepoint or a space).</summary>
    public readonly bool IsBlank => Codepoint == 0 || Codepoint == ' ';
}
