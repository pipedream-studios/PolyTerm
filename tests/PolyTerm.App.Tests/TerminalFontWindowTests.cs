using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers <see cref="TerminalFontWindow"/> populating from <see cref="App.Settings"/>. Like
/// <see cref="VaultSettingsWindowTests"/>, never clicks Save - <c>App.SetTerminalFont</c> writes the real
/// per-user settings.json - so this mutates the in-memory settings only and restores them afterwards.
/// </summary>
[Collection("AppSettings")]
public sealed class TerminalFontWindowTests : IDisposable
{
    private readonly string? _originalFamily = App.Settings.TerminalFontFamily;
    private readonly double _originalSize = App.Settings.TerminalFontSize;

    public void Dispose()
    {
        App.Settings.TerminalFontFamily = _originalFamily;
        App.Settings.TerminalFontSize = _originalSize;
    }

    private static TerminalFontWindow.FontChoice Selected(TerminalFontWindow w) =>
        (TerminalFontWindow.FontChoice)w.FindControl<ComboBox>("FontCombo")!.SelectedItem!;

    [AvaloniaFact]
    public void NoSavedFamily_SelectsTheDefaultForThisOS_AndListsEveryBundledFont()
    {
        App.Settings.TerminalFontFamily = null;
        App.Settings.TerminalFontSize = 15;

        var window = new TerminalFontWindow();
        var combo = window.FindControl<ComboBox>("FontCombo")!;

        Selected(window).Family.Should().BeNull();
        Selected(window).Label.Should().Contain(TerminalFonts.PlatformDefault);
        combo.Items.Cast<TerminalFontWindow.FontChoice>().Select(c => c.Family)
            .Should().ContainInOrder(TerminalFonts.Bundled);
        window.FindControl<NumericUpDown>("SizeBox")!.Value.Should().Be(15);
    }

    [AvaloniaFact]
    public void SavedBundledFamily_IsSelected()
    {
        App.Settings.TerminalFontFamily = "Hack";

        Selected(new TerminalFontWindow()).Family.Should().Be("Hack");
    }

    [AvaloniaFact]
    public void SavedInstalledFamily_IsSelectable_BeforeTheInstalledScanFinishes()
    {
        App.Settings.TerminalFontFamily = "Some Installed Mono";

        Selected(new TerminalFontWindow()).Family.Should().Be("Some Installed Mono");
    }
}
