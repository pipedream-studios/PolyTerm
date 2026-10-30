using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PolyTerm.App.Surfaces.Terminal;

namespace PolyTerm.App.Views;

/// <summary>
/// Picks the terminal font family and size (View → Terminal Font…). Lists a "default for this OS" entry,
/// the bundled families, then installed monospace families once a background scan finds them. Save
/// persists via <see cref="App.SetTerminalFont"/>, which also applies it to every open terminal tab.
/// </summary>
public sealed partial class TerminalFontWindow : Window
{
    /// <summary>One dropdown entry: <see cref="Family"/> null is the per-OS default.</summary>
    internal sealed record FontChoice(string Label, string? Family)
    {
        public override string ToString() => Label;
    }

    // The installed-font scan measures every system family; cached for the app's lifetime so reopening
    // this window is instant. Fonts installed while PolyTerm is running show up after a restart.
    private static IReadOnlyList<string>? s_installedCache;

    private readonly List<FontChoice> _choices = [];

    public TerminalFontWindow()
    {
        InitializeComponent();

        _choices.Add(new FontChoice($"Default for this OS ({TerminalFonts.PlatformDefault})", null));
        _choices.AddRange(TerminalFonts.Bundled.Select(f => new FontChoice(f, f)));

        // A saved installed font must be selectable before the scan finishes (or if it's since been uninstalled).
        var saved = App.Settings.TerminalFontFamily;
        if (saved is { Length: > 0 } && !TerminalFonts.IsBundled(saved))
            _choices.Add(new FontChoice(saved, saved));

        FontCombo.ItemsSource   = _choices.ToList();
        FontCombo.SelectedIndex = IndexOf(saved);
        SizeBox.Value           = (decimal)App.Settings.TerminalFontSize;
        UpdatePreview();

        _ = LoadInstalledAsync();
    }

    /// <summary>Index of <paramref name="family"/> in the current choices; the default entry for null or unknown.</summary>
    private int IndexOf(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return 0;
        var i = _choices.FindIndex(c => c.Family is not null && c.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    private async Task LoadInstalledAsync()
    {
        var installed = s_installedCache ??= await Task.Run(TerminalFonts.GetInstalledMonospace).ConfigureAwait(true);

        var selected = (FontCombo.SelectedItem as FontChoice)?.Family;
        foreach (var family in installed)
            if (!_choices.Any(c => c.Family is not null && c.Family.Equals(family, StringComparison.OrdinalIgnoreCase)))
                _choices.Add(new FontChoice($"{family} (installed)", family));

        FontCombo.ItemsSource   = _choices.ToList();
        FontCombo.SelectedIndex = IndexOf(selected);
        InstalledStatus.Text = installed.Count == 0
            ? "No other monospace fonts found on this system."
            : $"{installed.Count} installed monospace font{(installed.Count == 1 ? "" : "s")} listed after the bundled ones.";
    }

    private void FontCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void SizeBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewPanel is null) return; // ValueChanged can fire during InitializeComponent
        var family = TerminalFonts.Resolve((FontCombo.SelectedItem as FontChoice)?.Family);
        var size   = (double)(SizeBox.Value ?? 14);
        foreach (var line in PreviewPanel.Children.OfType<TextBlock>())
        {
            line.FontFamily = family;
            line.FontSize   = size;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        App.SetTerminalFont((FontCombo.SelectedItem as FontChoice)?.Family, (double)(SizeBox.Value ?? 14));
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
