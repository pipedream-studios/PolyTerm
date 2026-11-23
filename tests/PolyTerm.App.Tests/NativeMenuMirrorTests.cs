using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AwesomeAssertions;
using CommunityToolkit.Mvvm.Input;
using PolyTerm.App.Views;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>Covers the in-window menu → macOS global menu bar mirror (issue #307).</summary>
public sealed class NativeMenuMirrorTests
{
    [Theory]
    [InlineData("_File", "File")]
    [InlineData("Se_rial…", "Serial…")]
    [InlineData("HTTP_S…", "HTTPS…")]
    [InlineData("Send Ctrl+Alt+Delete", "Send Ctrl+Alt+Delete")]
    [InlineData("snake__case", "snake_case")]
    public void StripAccessKey_RemovesMnemonicUnderscores(string header, string expected) =>
        NativeMenuMirror.StripAccessKey(header).Should().Be(expected);

    [Fact]
    public void ToMacGesture_ReplacesCtrlWithCmd()
    {
        var mac = NativeMenuMirror.ToMacGesture(KeyGesture.Parse("Ctrl+Shift+K"));

        mac.Key.Should().Be(Key.K);
        mac.KeyModifiers.Should().Be(KeyModifiers.Meta | KeyModifiers.Shift);
    }

    [AvaloniaFact]
    public void Build_MirrorsStructureHeadersAndSeparators()
    {
        var menu = new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "_File",
                    Items =
                    {
                        new MenuItem { Header = "_Quick Connect…", InputGesture = KeyGesture.Parse("Ctrl+Shift+K") },
                        new Separator(),
                        new MenuItem { Header = "_New", Items = { new MenuItem { Header = "_SSH…" } } },
                    },
                },
            },
        };

        var native = NativeMenuMirror.Build(menu, mapGesture: NativeMenuMirror.ToMacGesture);

        var file = native.Items.Should().ContainSingle().Which.Should().BeOfType<NativeMenuItem>().Subject;
        file.Header.Should().Be("File");
        var items = file.Menu!.Items;
        items.Should().HaveCount(3);
        var quick = items[0].Should().BeOfType<NativeMenuItem>().Subject;
        quick.Header.Should().Be("Quick Connect…");
        quick.Gesture.Should().Be(new KeyGesture(Key.K, KeyModifiers.Meta | KeyModifiers.Shift));
        items[1].Should().BeOfType<NativeMenuItemSeparator>();
        ((NativeMenuItem)items[2]).Menu!.Items.Should().ContainSingle()
            .Which.Should().BeOfType<NativeMenuItem>().Which.Header.Should().Be("SSH…");
    }

    [AvaloniaFact]
    public void Click_RunsSourceHandlerWithTheMenuItemAsSender()
    {
        var source = new MenuItem { Header = "_SSH…", Tag = "ssh" };
        object? sender = null;
        source.Click += (s, _) => sender = s;
        var menu = new Menu { Items = { new MenuItem { Header = "_File", Items = { source } } } };

        var native = NativeMenuMirror.Build(menu);
        ((INativeMenuItemExporterEventsImplBridge)((NativeMenuItem)native.Items[0]).Menu!.Items[0]).RaiseClicked();

        sender.Should().BeSameAs(source);
        ((Control)sender!).Tag.Should().Be("ssh");
    }

    [AvaloniaFact]
    public void Click_RunsSourceCommandExactlyOnce()
    {
        var runs = 0;
        var source = new MenuItem { Header = "_Detached Sessions…", Command = new RelayCommand(() => runs++) };
        var menu = new Menu { Items = { new MenuItem { Header = "_Session", Items = { source } } } };

        var native = NativeMenuMirror.Build(menu);
        ((INativeMenuItemExporterEventsImplBridge)((NativeMenuItem)native.Items[0]).Menu!.Items[0]).RaiseClicked();

        runs.Should().Be(1);
    }

    [AvaloniaFact]
    public void SourceVisibilityAndEnabledChanges_FlowToNativeItem()
    {
        var source = new MenuItem { Header = "_Bitwarden Login…", IsVisible = false };
        var menu = new Menu { Items = { new MenuItem { Header = "_View", Items = { source } } } };

        var native = (NativeMenuItem)((NativeMenuItem)NativeMenuMirror.Build(menu).Items[0]).Menu!.Items[0];
        native.IsVisible.Should().BeFalse();

        source.IsVisible = true;
        source.IsEnabled = false;

        native.IsVisible.Should().BeTrue();
        native.IsEnabled.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Build_ExcludedItems_DropOrphanedSeparatorsAndEmptySubmenus()
    {
        var exit = new MenuItem { Header = "E_xit" };
        var about = new MenuItem { Header = "_About PolyTerm" };
        var menu = new Menu
        {
            Items =
            {
                new MenuItem { Header = "_File", Items = { new MenuItem { Header = "_Open" }, new Separator(), exit } },
                new MenuItem { Header = "_Help", Items = { about } },
            },
        };

        var native = NativeMenuMirror.Build(menu, exclude: i => i == exit || i == about);

        var file = native.Items.Should().ContainSingle().Which.Should().BeOfType<NativeMenuItem>().Subject;
        file.Header.Should().Be("File");
        file.Menu!.Items.Should().ContainSingle().Which.Should().BeOfType<NativeMenuItem>().Which.Header.Should().Be("Open");
    }
}
