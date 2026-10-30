using Avalonia.Media;

namespace PolyTerm.App.Surfaces.Terminal;

/// <summary>Cursor shapes the terminal can draw.</summary>
public enum TerminalCursorStyle { Underline, Bar, Block }

/// <summary>
/// A terminal colour scheme: default text/background, cursor and selection colours, and the 16 ANSI colours
/// (0-7 normal, 8-15 bright). The remote shell picks which ANSI colour its prompt, <c>ls</c> output etc. use;
/// the scheme decides what each one looks like. 256-colour and truecolor output isn't affected.
/// </summary>
public sealed record TerminalColorScheme(
    string Name, Color Foreground, Color Background, Color Cursor, Color Selection, IReadOnlyList<Color> Ansi)
{
    /// <summary>The default for new installs: VS Code's Dark+ terminal palette on PolyTerm's existing background.</summary>
    public const string DefaultName = "PolyTerm Dark";

    private static TerminalColorScheme Make(string name, string fg, string bg, string cursor, string selection, params string[] ansi) =>
        new(name, Color.Parse(fg), Color.Parse(bg), Color.Parse(cursor), Color.Parse(selection), ansi.Select(Color.Parse).ToArray());

    /// <summary>Built-in schemes, in display order.</summary>
    public static IReadOnlyList<TerminalColorScheme> BuiltIn { get; } =
    [
        Make(DefaultName, "#CCCCCC", "#1E1E1E", "#FFFFFF", "#264F78",
            "#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
            "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#E5E5E5"),
        // The palette PolyTerm shipped with before schemes existed (classic VGA), kept for anyone used to it.
        Make("PolyTerm Classic", "#CCCCCC", "#1E1E1E", "#FFFFFF", "#264F78",
            "#000000", "#AA0000", "#00AA00", "#AAAA00", "#0000AA", "#AA00AA", "#00AAAA", "#AAAAAA",
            "#555555", "#FF5555", "#55FF55", "#FFFF55", "#5555FF", "#FF55FF", "#55FFFF", "#FFFFFF"),
        Make("Campbell", "#CCCCCC", "#0C0C0C", "#FFFFFF", "#3A3A3A",
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"),
        Make("One Half Dark", "#DCDFE4", "#282C34", "#FFFFFF", "#3E4451",
            "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
            "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4"),
        Make("Dracula", "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
            "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
            "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"),
        Make("Nord", "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E",
            "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
            "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"),
        Make("Gruvbox Dark", "#EBDBB2", "#282828", "#EBDBB2", "#504945",
            "#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984",
            "#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2"),
        Make("Solarized Dark", "#839496", "#002B36", "#93A1A1", "#073642",
            "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
            "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"),
        Make("Solarized Light", "#657B83", "#FDF6E3", "#586E75", "#EEE8D5",
            "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
            "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"),
        Make("Tango Dark", "#D3D7CF", "#000000", "#FFFFFF", "#3465A4",
            "#000000", "#CC0000", "#4E9A06", "#C4A000", "#3465A4", "#75507B", "#06989A", "#D3D7CF",
            "#555753", "#EF2929", "#8AE234", "#FCE94F", "#729FCF", "#AD7FA8", "#34E2E2", "#EEEEEC"),
        Make("Tango Light", "#555753", "#FFFFFF", "#000000", "#C7DBF0",
            "#000000", "#CC0000", "#4E9A06", "#C4A000", "#3465A4", "#75507B", "#06989A", "#D3D7CF",
            "#555753", "#EF2929", "#8AE234", "#FCE94F", "#729FCF", "#AD7FA8", "#34E2E2", "#EEEEEC"),
    ];

    public static TerminalColorScheme Default { get; } = BuiltIn[0];

    /// <summary>The previous hardcoded palette - see <see cref="BuiltIn"/>.</summary>
    public static TerminalColorScheme Classic { get; } = BuiltIn[1];

    /// <summary>The built-in scheme called <paramref name="name"/> (case-insensitive), or <see cref="Default"/> for null/unknown.</summary>
    public static TerminalColorScheme Find(string? name) =>
        BuiltIn.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>Whether <paramref name="name"/> is a built-in scheme (case-insensitive).</summary>
    public static bool Exists(string? name) =>
        BuiltIn.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
