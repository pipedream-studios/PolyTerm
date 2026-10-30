using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Moq;
using PolyTerm.App.Serial;
using PolyTerm.App.Surfaces.Terminal;
using PolyTerm.App.ViewModels;
using PolyTerm.App.Views;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Covers tab customisation (rename, tab colour, the header's right-click menu) and the per-connection
/// appearance settings ("colorScheme" and "tabColor" in the profile, edited on the connection's Organization tab).
/// </summary>
public sealed class TabAndConnectionAppearanceTests
{
    private sealed class FakeComPortWatcher : IComPortWatcher
    {
        public IReadOnlyList<PolyTerm.Protocols.Serial.DetectedComPort> CurrentPorts { get; } = [];
#pragma warning disable CS0067
        public event EventHandler? PortsChanged;
#pragma warning restore CS0067
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
#pragma warning disable CS0067
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
#pragma warning restore CS0067
        public string TerminalType => "xterm-256color";
        public ValueTask SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeProtocolSession(ConnectionProfile profile) : IProtocolSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ConnectionProfile Profile { get; } = profile;
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ITerminalChannel? Terminal { get; } = new FakeTerminalChannel();
        public IFileTransferChannel? FileTransfer => null;
        public IFramebufferChannel? Framebuffer => null;
        public ITunnelManager? Tunnels => null;
        public INativeHostChannel? NativeHost => null;
        public IWebChannel? Web => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ConnectionProfile Profile(params (string Key, string Value)[] settings) => new()
    {
        Name = "prod", ProtocolId = "ssh",
        Settings = settings.ToDictionary(s => s.Key, s => JsonSerializer.SerializeToElement(s.Value)),
    };

    private static SessionTabViewModel Tab(ConnectionProfile? profile = null) =>
        new(new FakeProtocolSession(profile ?? Profile()));

    // ─── Tab title / colour ────────────────────────────────────────────────

    [AvaloniaFact]
    public void CustomTitle_OverridesTheAutomaticTitle_AndClearingItRestoresIt()
    {
        var tab = Tab();
        tab.OscTitle = "rob@prod: ~";

        tab.CustomTitle = "Prod DB";
        tab.Title.Should().Be("Prod DB", "a name the user gave the tab beats the OSC title the shell sends");

        tab.CustomTitle = null;
        tab.Title.Should().Be("rob@prod: ~");
    }

    [AvaloniaFact]
    public void TabColor_StartsFromTheConnectionsSetting()
    {
        var tab = Tab(Profile(("tabColor", "red")));

        tab.TabColor.Should().Be("Red", "normalised to the palette's own spelling");
        tab.HasTabColor.Should().BeTrue();
    }

    [AvaloniaFact]
    public void SetTabColor_WithAnUnknownName_ResetsToTheThemeDefault()
    {
        var tab = Tab(Profile(("tabColor", "Blue")));

        tab.SetTabColorCommand.Execute("Chartreuse");

        tab.TabColor.Should().BeNull();
        tab.HasTabColor.Should().BeFalse();
        tab.TabColorBrush.Should().BeNull();
    }

    [AvaloniaFact]
    public void TabHeaderMenu_ListsRenameColourAndClose_WithTheCurrentColourTicked()
    {
        var tab = Tab(Profile(("tabColor", "Green")));
        SessionTabViewModel? renamed = null;

        var menu = TabHeaderMenu.Build(tab, t => renamed = t);
        var items = menu.Items.OfType<MenuItem>().ToList();

        items.Select(i => i.Header).Should().Equal("Rename…", "Tab Colour", "Close");

        var colours = items[1].Items.OfType<MenuItem>().ToList();
        colours.Select(i => i.Header).Should().Equal(["Default", .. TabColors.All.Select(c => c.Name)]);
        colours.Where(i => i.IsChecked).Select(i => i.Header).Should().Equal("Green");

        colours.Single(i => (string)i.Header! == "Purple").Command!.Execute("Purple");
        tab.TabColor.Should().Be("Purple");

        items[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        renamed.Should().BeSameAs(tab);
    }

    // ─── Per-connection colour scheme ─────────────────────────────────────

    [Fact]
    public void ResolveColorScheme_ConnectionOverrideWins_OverTheAppWideScheme()
    {
        SessionSurfaceTemplateSelector.ResolveColorScheme(Profile(("colorScheme", "Solarized Light")), "Dracula")
            .Name.Should().Be("Solarized Light");
    }

    [Fact]
    public void ResolveColorScheme_NoOrUnknownOverride_UsesTheAppWideScheme()
    {
        SessionSurfaceTemplateSelector.ResolveColorScheme(Profile(), "Dracula").Name.Should().Be("Dracula");
        SessionSurfaceTemplateSelector.ResolveColorScheme(Profile(("colorScheme", "Not A Scheme")), "Nord").Name.Should().Be("Nord");
        SessionSurfaceTemplateSelector.ResolveColorScheme(null, null).Name.Should().Be(TerminalColorScheme.DefaultName);
    }

    // ─── Connection editor ────────────────────────────────────────────────

    private static (NewConnectionViewModel Vm, Func<ConnectionProfile?> Saved) MakeEditor(ConnectionProfile? existing = null)
    {
        ConnectionProfile? saved = null;
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, CancellationToken>((p, _) => saved = p)
            .Returns<ConnectionProfile, CancellationToken>((p, _) => Task.FromResult(p));

        string? reason = null;
        var ssh = new Mock<IProtocolDescriptor>();
        ssh.Setup(d => d.Id).Returns("ssh");
        ssh.Setup(d => d.DisplayName).Returns("SSH");
        ssh.Setup(d => d.DefaultPort).Returns(22);
        ssh.Setup(d => d.IsAvailable(out reason)).Returns(true);
        var registry = new Mock<IProtocolRegistry>();
        registry.Setup(r => r.All).Returns([ssh.Object]);
        registry.Setup(r => r.Find(It.IsAny<string>())).Returns((IProtocolDescriptor?)null);

        var tree = new ConnectionTreeViewModel(store.Object, new Mock<ISessionManager>().Object, registry.Object, new FakeComPortWatcher());
        return (new NewConnectionViewModel(store.Object, tree, registry.Object, existing, credentialProviders: []), () => saved);
    }

    [AvaloniaFact]
    public async Task Editor_SavesSchemeAndTabColour_AndOmitsThemWhenLeftAtInheritAndNone()
    {
        var (vm, saved) = MakeEditor();
        vm.Name = "prod";
        vm.Host = "prod.example.com";
        vm.SelectedColorScheme = "Gruvbox Dark";
        vm.SelectedTabColor = "Red";

        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings["colorScheme"].GetString().Should().Be("Gruvbox Dark");
        saved()!.Settings["tabColor"].GetString().Should().Be("Red");

        vm.SelectedColorScheme = "Inherit";
        vm.SelectedTabColor = "None";
        await vm.SaveCommand.ExecuteAsync(null);

        saved()!.Settings.Should().NotContainKey("colorScheme", "Inherit means no override, so nothing is stored");
        saved()!.Settings.Should().NotContainKey("tabColor");
    }

    [AvaloniaFact]
    public void Editor_LoadsAnExistingProfilesSchemeAndTabColour()
    {
        var (vm, _) = MakeEditor(Profile(("colorScheme", "nord"), ("tabColor", "teal")));

        vm.SelectedColorScheme.Should().Be("Nord");
        vm.SelectedTabColor.Should().Be("Teal");
    }
}
