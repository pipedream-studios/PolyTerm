using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers the macOS app menu showing About PolyTerm rather than Avalonia's About Avalonia (issue #307).</summary>
public sealed class MacAppMenuTests
{
    [AvaloniaFact]
    public void InstallMacAppMenu_ReplacesAvaloniasDefaultItemsInPlace()
    {
        // Avalonia's native exporter has already set this default by the time App runs, and doesn't
        // pick up a replacement menu - so the same menu object must be the one that changes.
        var app = Application.Current!;
        var avaloniaDefault = new NativeMenu { Items = { new NativeMenuItem("About Avalonia") } };
        NativeMenu.SetMenu(app, avaloniaDefault);

        App.InstallMacAppMenu(app);

        NativeMenu.GetMenu(app).Should().BeSameAs(avaloniaDefault);
        avaloniaDefault.Items.Should().ContainSingle()
            .Which.Should().BeOfType<NativeMenuItem>().Which.Header.Should().Be("About PolyTerm");
    }

    [AvaloniaFact]
    public void InstallMacAppMenu_SetsMenuWhenNoneExists()
    {
        var app = Application.Current!;
        NativeMenu.SetMenu(app, null);

        App.InstallMacAppMenu(app);

        NativeMenu.GetMenu(app)!.Items.Should().ContainSingle()
            .Which.Should().BeOfType<NativeMenuItem>().Which.Header.Should().Be("About PolyTerm");
    }
}
