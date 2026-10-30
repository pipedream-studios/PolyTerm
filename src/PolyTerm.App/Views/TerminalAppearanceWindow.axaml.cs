using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PolyTerm.App.Surfaces.Terminal;

namespace PolyTerm.App.Views;

/// <summary>
/// Terminal appearance settings (View → Terminal Appearance…): font family and size, colour scheme and cursor
/// style, with a live preview. The font list is a "default for this OS" entry, the bundled families, then
/// installed monospace families once a background scan finds them. Save persists via
/// <see cref="App.SetTerminalAppearance"/>, which also applies it to every open terminal tab.
/// </summary>
public sealed partial class TerminalAppearanceWindow : Window
{
    /// <summary>One font dropdown entry: <see cref="Family"/> null is the per-OS default.</summary>
    internal sealed record FontChoice(string Label, string? Family)
    {
        public override string ToString() => Label;
    }

    // The installed-font scan measures every system family; cached for the app's lifetime so reopening
    // this window is instant. Fonts installed while PolyTerm is running show up after a restart.
    private static IReadOnlyList<string>? s_installedCache;

    private readonly List<FontChoice> _fonts = [];

    public TerminalAppearanceWindow()
    {
        InitializeComponent();

        _fonts.Add(new FontChoice($"Default for this OS ({TerminalFonts.PlatformDefault})", null));
        _fonts.AddRange(TerminalFonts.Bundled.Select(f => new FontChoice(f, f)));

        // A saved installed font must be selectable before the scan finishes (or if it's since been uninstalled).
        var saved = App.Settings.TerminalFontFamily;
        if (saved is { Length: > 0 } && !TerminalFonts.IsBundled(saved))
            _fonts.Add(new FontChoice(saved, saved));

        FontCombo.ItemsSource   = _fonts.ToList();
        FontCombo.SelectedIndex = IndexOf(saved);
        SizeBox.Value           = (decimal)App.Settings.TerminalFontSize;

        SchemeCombo.ItemsSource  = TerminalColorScheme.BuiltIn.Select(s => s.Name).ToList();
        SchemeCombo.SelectedItem = TerminalColorScheme.Find(App.Settings.TerminalColorScheme).Name;

        CursorCombo.ItemsSource  = Enum.GetNames<TerminalCursorStyle>();
        CursorCombo.SelectedItem = Enum.TryParse<TerminalCursorStyle>(App.Settings.TerminalCursorStyle, true, out var cursor)
            ? cursor.ToString()
            : nameof(TerminalCursorStyle.Underline);

        UpdatePreview();
        _ = LoadInstalledAsync();
    }

    internal string? SelectedFamily => (FontCombo.SelectedItem as FontChoice)?.Family;

    internal string SelectedScheme => SchemeCombo.SelectedItem as string ?? TerminalColorScheme.DefaultName;

    internal string SelectedCursor => CursorCombo.SelectedItem as string ?? nameof(TerminalCursorStyle.Underline);

    /// <summary>Index of <paramref name="family"/> in the font choices; the default entry for null or unknown.</summary>
    private int IndexOf(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return 0;
        var i = _fonts.FindIndex(c => c.Family is not null && c.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    private async Task LoadInstalledAsync()
    {
        var installed = s_installedCache ??= await Task.Run(TerminalFonts.GetInstalledMonospace).ConfigureAwait(true);

        var selected = SelectedFamily;
        foreach (var family in installed)
            if (!_fonts.Any(c => c.Family is not null && c.Family.Equals(family, StringComparison.OrdinalIgnoreCase)))
                _fonts.Add(new FontChoice($"{family} (installed)", family));

        FontCombo.ItemsSource   = _fonts.ToList();
        FontCombo.SelectedIndex = IndexOf(selected);
        InstalledStatus.Text = installed.Count == 0
            ? "No other monospace fonts found on this system."
            : $"{installed.Count} installed monospace font{(installed.Count == 1 ? "" : "s")} listed after the bundled ones.";
    }

    private void Input_Changed(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void SizeBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdatePreview();

    /// <summary>
    /// Redraws the preview as a small fake session in the chosen scheme: a coloured prompt, typed text in the
    /// default foreground, <c>ls</c>-style coloured output, the 16 ANSI colours, and the cursor.
    /// </summary>
    private void UpdatePreview()
    {
        if (PreviewPanel is null || SchemeCombo is null || CursorCombo is null) return; // events can fire during InitializeComponent

        var scheme = TerminalColorScheme.Find(SelectedScheme);
        var family = TerminalFonts.Resolve(SelectedFamily);
        var size   = (double)(SizeBox.Value ?? 14);
        IBrush Ansi(int i) => new SolidColorBrush(scheme.Ansi[i]);
        var fg = new SolidColorBrush(scheme.Foreground);

        PreviewBorder.Background = new SolidColorBrush(scheme.Background);
        PreviewPanel.Children.Clear();

        TextBlock Line(params Inline[] inlines)
        {
            var tb = new TextBlock { FontFamily = family, FontSize = size, Foreground = fg };
            tb.Inlines!.AddRange(inlines);
            PreviewPanel.Children.Add(tb);
            return tb;
        }
        Run R(string text, IBrush? brush = null, bool bold = false) =>
            new(text) { Foreground = brush ?? fg, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal };

        Line(R("rob@destiny", Ansi(10), bold: true), R(":"), R("~/projects", Ansi(12), bold: true), R("$ "), R("ls -l"));
        Line(R("drwxr-xr-x 2 rob rob 4096 Oct 30 "), R("docs", Ansi(12), bold: true));
        Line(R("-rwxr-xr-x 1 rob rob 2290 Oct 30 "), R("build.sh", Ansi(10), bold: true));
        Line(R("-rw-r--r-- 1 rob rob 7172 Oct 30 README.md"));
        Line(R("error: ", Ansi(9), bold: true), R("warning: ", Ansi(11), bold: true), R("0O 1lI| {}[]() => != ┌─┐ ✓"));

        var cursorLine = Line(R("rob@destiny", Ansi(10), bold: true), R(":"), R("~", Ansi(12), bold: true), R("$ "));
        var cursorStyle = Enum.Parse<TerminalCursorStyle>(SelectedCursor);
        var cellWidth = size * 0.6;
        var cursor = new Rectangle
        {
            Fill = new SolidColorBrush(scheme.Cursor),
            Width  = cursorStyle == TerminalCursorStyle.Bar ? 2 : cellWidth,
            Height = cursorStyle == TerminalCursorStyle.Underline ? 2 : size * 1.2,
            VerticalAlignment = cursorStyle == TerminalCursorStyle.Underline ? VerticalAlignment.Bottom : VerticalAlignment.Center,
        };
        PreviewPanel.Children.Remove(cursorLine);
        PreviewPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { cursorLine, cursor } });

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new(0, 8, 0, 0) };
        for (var i = 0; i < 16; i++)
            swatches.Children.Add(new Rectangle { Width = 18, Height = 10, Fill = Ansi(i), [ToolTip.TipProperty] = $"ANSI {i}" });
        PreviewPanel.Children.Add(swatches);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var scheme = SelectedScheme == TerminalColorScheme.DefaultName ? null : SelectedScheme;
        App.SetTerminalAppearance(SelectedFamily, (double)(SizeBox.Value ?? 14), scheme, SelectedCursor);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
