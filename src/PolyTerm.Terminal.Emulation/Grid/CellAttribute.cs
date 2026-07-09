namespace PolyTerm.Terminal;

/// <summary>
/// Visual attributes for a terminal cell (SGR). The boolean fields each correspond to one
/// standard SGR attribute (e.g. ESC[1m for <see cref="Bold"/>, ESC[3m for <see cref="Italic"/>).
/// </summary>
public struct CellAttribute : IEquatable<CellAttribute>
{
    /// <summary>Foreground (text) color.</summary>
    public TerminalColor Foreground;

    /// <summary>Background color.</summary>
    public TerminalColor Background;

    /// <summary>SGR 1 — bold/increased intensity.</summary>
    public bool Bold;

    /// <summary>SGR 2 — dim/decreased intensity.</summary>
    public bool Dim;

    /// <summary>SGR 3 — italic.</summary>
    public bool Italic;

    /// <summary>SGR 4 — underline.</summary>
    public bool Underline;

    /// <summary>SGR 21 — double underline.</summary>
    public bool DoubleUnderline;

    /// <summary>SGR 5 — slow blink.</summary>
    public bool Blink;

    /// <summary>SGR 6 — rapid blink.</summary>
    public bool RapidBlink;

    /// <summary>SGR 7 — reverse video (swap foreground/background).</summary>
    public bool Reverse;

    /// <summary>SGR 8 — invisible/hidden text.</summary>
    public bool Invisible;

    /// <summary>SGR 9 — strikethrough.</summary>
    public bool Strikethrough;

    /// <summary>The unstyled cell attribute: default foreground/background, no SGR flags set.</summary>
    public static readonly CellAttribute Default = new()
    {
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
    };

    public readonly bool Equals(CellAttribute other) =>
        Foreground == other.Foreground &&
        Background == other.Background &&
        Bold == other.Bold &&
        Dim == other.Dim &&
        Italic == other.Italic &&
        Underline == other.Underline &&
        Blink == other.Blink &&
        Reverse == other.Reverse &&
        Invisible == other.Invisible &&
        Strikethrough == other.Strikethrough;

    public override readonly bool Equals(object? obj) => obj is CellAttribute a && Equals(a);
    public override readonly int GetHashCode() => HashCode.Combine(Foreground, Background, Bold, Italic, Underline, Reverse);
    public static bool operator ==(CellAttribute l, CellAttribute r) => l.Equals(r);
    public static bool operator !=(CellAttribute l, CellAttribute r) => !l.Equals(r);
}

/// <summary>
/// A terminal color in one of four SGR color spaces: the default color, a named 16-color
/// (ESC[30-37m/40-47m or ESC[90-97m/100-107m), an indexed 256-color, or a 24-bit TrueColor RGB value.
/// Which fields are meaningful depends on <see cref="Kind"/>.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    /// <summary>The terminal's default foreground/background color (no SGR color code applied).</summary>
    public static readonly TerminalColor Default = new(ColorKind.Default, 0, 0, 0);

    /// <summary>Which color space this value uses.</summary>
    public ColorKind Kind { get; }

    // For named/indexed colors
    /// <summary>Palette index, meaningful when <see cref="Kind"/> is <see cref="ColorKind.Named"/> or <see cref="ColorKind.Indexed"/>.</summary>
    public byte Index { get; }

    // For TrueColor (RGB)
    /// <summary>Red channel, meaningful when <see cref="Kind"/> is <see cref="ColorKind.TrueColor"/>.</summary>
    public byte R { get; }

    /// <summary>Green channel, meaningful when <see cref="Kind"/> is <see cref="ColorKind.TrueColor"/>.</summary>
    public byte G { get; }

    /// <summary>Blue channel, meaningful when <see cref="Kind"/> is <see cref="ColorKind.TrueColor"/>.</summary>
    public byte B { get; }

    private TerminalColor(ColorKind kind, byte index, byte r, byte g) : this(kind, index, r, g, 0) { }
    private TerminalColor(ColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind; Index = index; R = r; G = g; B = b;
    }

    /// <summary>Creates a named 16-color value (SGR 30-37/40-47, 90-97/100-107; index 0-15).</summary>
    public static TerminalColor Named(byte index) => new(ColorKind.Named, index, 0, 0);

    /// <summary>Creates an indexed 256-color value (SGR 38;5;n / 48;5;n).</summary>
    public static TerminalColor Indexed(byte index) => new(ColorKind.Indexed, index, 0, 0);

    /// <summary>Creates a 24-bit TrueColor value (SGR 38;2;r;g;b / 48;2;r;g;b).</summary>
    public static TerminalColor Rgb(byte r, byte g, byte b) => new(ColorKind.TrueColor, 0, r, g, b);

    /// <inheritdoc/>
    public bool Equals(TerminalColor other) =>
        Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Index, R, G, B);
    /// <summary>Value equality — same <see cref="Kind"/> and matching fields for that kind.</summary>
    public static bool operator ==(TerminalColor l, TerminalColor r) => l.Equals(r);
    /// <summary>Negation of the equality operator.</summary>
    public static bool operator !=(TerminalColor l, TerminalColor r) => !l.Equals(r);
}

/// <summary>The color space a <see cref="TerminalColor"/> value is expressed in.</summary>
public enum ColorKind : byte
{
    /// <summary>The terminal's default color (no explicit SGR color code).</summary>
    Default,

    /// <summary>One of the 16 named ANSI colors.</summary>
    Named,

    /// <summary>One of the 256 xterm palette colors.</summary>
    Indexed,

    /// <summary>A 24-bit RGB color.</summary>
    TrueColor
}
