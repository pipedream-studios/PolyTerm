using System.Globalization;
using Avalonia.Media;

namespace PolyTerm.App.Surfaces.Terminal;

/// <summary>
/// The terminal's font choices: monospace families bundled with the app (Assets/Fonts, so they look the
/// same on every OS), the per-OS default used until the user picks one, and the installed monospace
/// families the user can pick instead.
/// </summary>
public static class TerminalFonts
{
    private const string BundledFontsUri = "avares://PolyTerm.App.Surfaces/Assets/Fonts";

    /// <summary>Family names of the bundled fonts (Regular + Bold each), in display order. Licences are in Assets/Fonts/licenses.</summary>
    public static IReadOnlyList<string> Bundled { get; } =
    [
        "Cascadia Mono",
        "Fira Code",
        "Hack",
        "IBM Plex Mono",
        "JetBrains Mono",
        "Source Code Pro",
    ];

    /// <summary>Bundled family used when a saved font isn't available on this machine.</summary>
    public const string Fallback = "JetBrains Mono";

    /// <summary>
    /// The default when no font has been chosen: Cascadia Mono on Windows (Windows Terminal's font, bundled
    /// so pre-Windows 11 machines get it too), Menlo on macOS (present on every Mac, Terminal.app's
    /// default), and bundled JetBrains Mono on Linux, where no monospace family is guaranteed installed.
    /// </summary>
    public static string PlatformDefault => DefaultFor(
        OperatingSystem.IsWindows() ? OSPlatformKind.Windows :
        OperatingSystem.IsMacOS()   ? OSPlatformKind.MacOS   : OSPlatformKind.Other);

    internal enum OSPlatformKind { Windows, MacOS, Other }

    internal static string DefaultFor(OSPlatformKind os) => os switch
    {
        OSPlatformKind.Windows => "Cascadia Mono",
        OSPlatformKind.MacOS   => "Menlo",
        _                      => Fallback,
    };

    /// <summary>
    /// <see cref="PlatformDefault"/> as a <see cref="FontFamily"/>, without the installed-font check - safe to
    /// evaluate before Avalonia's font manager exists (it's <see cref="TerminalSurface"/>'s property default).
    /// Menlo, the only non-bundled default, ships with every macOS.
    /// </summary>
    public static FontFamily DefaultFamily => Resolve(null, _ => true);

    public static bool IsBundled(string name) => Bundled.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a saved family name into a <see cref="FontFamily"/>: null/empty means <see cref="PlatformDefault"/>,
    /// a bundled name loads from the app's own resources, and an installed name is used as-is. A name that
    /// is neither (e.g. a settings file copied from a Mac to Linux) falls back to <see cref="Fallback"/>
    /// rather than to Avalonia's default UI font, which isn't monospace.
    /// </summary>
    public static FontFamily Resolve(string? name) => Resolve(name, IsInstalled);

    internal static FontFamily Resolve(string? name, Func<string, bool> isInstalled)
    {
        var family = string.IsNullOrWhiteSpace(name) ? PlatformDefault : name.Trim();

        if (IsBundled(family))
            return FromBundle(family);

        return isInstalled(family) ? new FontFamily(family) : FromBundle(Fallback);
    }

    private static FontFamily FromBundle(string family) =>
        new($"{BundledFontsUri}#{Bundled.First(b => b.Equals(family, StringComparison.OrdinalIgnoreCase))}");

    private static bool IsInstalled(string family) =>
        FontManager.Current.SystemFonts.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Installed font families whose "i" and "M" have the same advance width, sorted by name, excluding the
    /// bundled ones (already listed separately). Measured once per call, so callers should cache it - a
    /// few hundred families on a typical Mac take a noticeable fraction of a second.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledMonospace()
    {
        var result = new List<string>();
        foreach (var family in FontManager.Current.SystemFonts)
        {
            if (IsBundled(family.Name)) continue;
            try
            {
                var typeface = new Typeface(family);
                if (!FontManager.Current.TryGetGlyphTypeface(typeface, out _)) continue;
                if (Math.Abs(Measure("iiiiiiiiii", typeface) - Measure("MMMMMMMMMM", typeface)) < 0.5)
                    result.Add(family.Name);
            }
            catch (Exception)
            {
                // A broken or unreadable font file shouldn't take the menu down with it - just leave it out.
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static double Measure(string text, Typeface typeface) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 20, Brushes.White).Width;
}
