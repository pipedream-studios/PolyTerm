namespace PolyTerm.App.CommandPalette;

/// <summary>
/// Pure fuzzy-match/ranking logic for the command palette — no UI dependency, so it's directly
/// unit-testable. Matches VS Code's "Ctrl+Shift+P" model: case-insensitive substring beats a looser
/// in-order subsequence match ("nlsh" matches "New Local Shell"), and an empty query returns
/// everything in its original (already-sensible, hand-ordered) order.
/// </summary>
internal static class CommandPaletteFilter
{
    public static IReadOnlyList<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> commands, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return commands;

        var trimmed = query.Trim();
        return commands
            .Select(c => (Command: c, Score: Score(c.SearchableText, trimmed)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Command)
            .ToList();
    }

    /// <summary>
    /// Higher is better; null means "no match, exclude". A substring match scores by how early it
    /// starts (earlier is better, e.g. matching the very start of the title beats matching mid-word).
    /// A subsequence-only match (query's characters appear in order, not necessarily contiguous)
    /// scores much lower, so exact/substring hits always sort above loose fuzzy ones.
    /// </summary>
    private static int? Score(string text, string query)
    {
        var substringIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (substringIndex >= 0)
            return 10_000 - substringIndex;

        var subsequenceScore = SubsequenceScore(text, query);
        return subsequenceScore is null ? null : subsequenceScore.Value;
    }

    /// <summary>Returns a score if every character of <paramref name="query"/> appears in <paramref name="text"/> in order (case-insensitive), tighter clusters scoring higher; null if not all characters are found.</summary>
    private static int? SubsequenceScore(string text, string query)
    {
        var textPos = 0;
        var firstMatch = -1;
        var lastMatch = -1;
        foreach (var ch in query)
        {
            var found = -1;
            for (var i = textPos; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(ch))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0) return null;
            if (firstMatch < 0) firstMatch = found;
            lastMatch = found;
            textPos = found + 1;
        }

        var span = lastMatch - firstMatch + 1;
        // Shorter span (characters clustered together) and an earlier start both rank higher, but
        // always below any real substring match (capped well under Score's substring range).
        return 1_000 - span - firstMatch / 10;
    }
}
