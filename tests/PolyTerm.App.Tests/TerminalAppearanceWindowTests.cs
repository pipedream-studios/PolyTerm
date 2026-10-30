using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="TerminalAppearanceWindow"/> populating from <see cref="App.Settings"/>. Like
/// <see cref="VaultSettingsWindowTests"/>, never clicks Save - <c>App.SetTerminalAppearance</c> writes the real
/// per-user settings.json - so this mutates the in-memory settings only and restores them afterwards.
/// </summary>
[Collection("AppSettings")]
public sealed class TerminalAppearanceWindowTests : IDisposable
{
    private readonly string? _originalFamily = App.Settings.TerminalFontFamily;
    private readonly double _originalSize = App.Settings.TerminalFontSize;
    private readonly string? _originalScheme = App.Settings.TerminalColorScheme;
    private readonly string _originalCursor = App.Settings.TerminalCursorStyle;

    public void Dispose()
    {
        App.Settings.TerminalFontFamily = _originalFamily;
        App.Settings.TerminalFontSize = _originalSize;
        App.Settings.TerminalColorScheme = _originalScheme;
        App.Settings.TerminalCursorStyle = _originalCursor;
    }

    private static TerminalAppearanceWindow.FontChoice Selected(TerminalAppearanceWindow w) =>
        (TerminalAppearanceWindow.FontChoice)w.FindControl<ComboBox>("FontCombo")!.SelectedItem!;

    [AvaloniaFact]
    public void NoSavedFamily_SelectsTheDefaultForThisOS_AndListsEveryBundledFont()
    {
        App.Settings.TerminalFontFamily = null;
        App.Settings.TerminalFontSize = 15;

        var window = new TerminalAppearanceWindow();
        var combo = window.FindControl<ComboBox>("FontCombo")!;

        Selected(window).Family.Should().BeNull();
        Selected(window).Label.Should().Contain(TerminalFonts.PlatformDefault);
        combo.Items.Cast<TerminalAppearanceWindow.FontChoice>().Select(c => c.Family)
            .Should().ContainInOrder(TerminalFonts.Bundled);
        window.FindControl<NumericUpDown>("SizeBox")!.Value.Should().Be(15);
    }

    [AvaloniaFact]
    public void SavedBundledFamily_IsSelected()
    {
        App.Settings.TerminalFontFamily = "Hack";

        Selected(new TerminalAppearanceWindow()).Family.Should().Be("Hack");
    }

    [AvaloniaFact]
    public void SavedInstalledFamily_IsSelectable_BeforeTheInstalledScanFinishes()
    {
        App.Settings.TerminalFontFamily = "Some Installed Mono";

        Selected(new TerminalAppearanceWindow()).Family.Should().Be("Some Installed Mono");
    }

    [AvaloniaFact]
    public void NoSavedScheme_SelectsTheDefaultScheme_AndListsEveryBuiltIn()
    {
        App.Settings.TerminalColorScheme = null;

        var window = new TerminalAppearanceWindow();

        window.SelectedScheme.Should().Be(TerminalColorScheme.DefaultName);
        window.FindControl<ComboBox>("SchemeCombo")!.Items.Cast<string>()
            .Should().Equal(TerminalColorScheme.BuiltIn.Select(s => s.Name));
    }

    [AvaloniaFact]
    public void SavedSchemeAndCursor_AreSelected()
    {
        App.Settings.TerminalColorScheme = "dracula";
        App.Settings.TerminalCursorStyle = "Block";

        var window = new TerminalAppearanceWindow();

        window.SelectedScheme.Should().Be("Dracula", "the scheme's canonical name, matched case-insensitively");
        window.SelectedCursor.Should().Be("Block");
    }

    [AvaloniaFact]
    public void UnknownSavedCursor_FallsBackToUnderline()
    {
        App.Settings.TerminalCursorStyle = "Squiggle";

        new TerminalAppearanceWindow().SelectedCursor.Should().Be("Underline");
    }
}
