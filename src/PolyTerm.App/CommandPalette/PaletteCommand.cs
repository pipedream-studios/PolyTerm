namespace PolyTerm.App.CommandPalette;

/// <summary>One action reachable from the command palette (Ctrl+Shift+P).</summary>
public sealed record PaletteCommand(string Title, string Category, string? Shortcut, Action Execute)
{
    /// <summary>What <see cref="CommandPaletteFilter"/> matches search text against — "Category: Title", matching how the list displays it.</summary>
    public string SearchableText => $"{Category}: {Title}";
}
