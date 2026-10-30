using Avalonia.Media;

namespace PolyTerm.App.ViewModels;

/// <summary>
/// Named colours a session tab's header text can use - set per tab (right-click the tab) or as a connection's
/// default (its "tabColor" setting, e.g. red for production). Stored by name so a palette tweak here changes
/// every saved profile with it. Mid-saturation shades chosen to read on both the dark and light app themes.
/// </summary>
public static class TabColors
{
    public static IReadOnlyList<(string Name, Color Color)> All { get; } =
    [
        ("Red",    Color.Parse("#E5484D")),
        ("Orange", Color.Parse("#E5853B")),
        ("Yellow", Color.Parse("#C9A227")),
        ("Green",  Color.Parse("#46A758")),
        ("Teal",   Color.Parse("#12A594")),
        ("Blue",   Color.Parse("#3E8EED")),
        ("Purple", Color.Parse("#8E4EC6")),
        ("Pink",   Color.Parse("#D6409F")),
    ];

    /// <summary>The colour called <paramref name="name"/> (case-insensitive), or null for null/unknown.</summary>
    public static Color? Find(string? name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } match
            ? match.Color
            : null;

    /// <summary>The canonical spelling of <paramref name="name"/>, or null if it isn't one of <see cref="All"/>.</summary>
    public static string? Normalize(string? name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Name;
}
