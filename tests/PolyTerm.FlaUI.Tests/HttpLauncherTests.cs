using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the HTTP/S launcher — closing the "HTTP/S launcher — New
/// Connection → HTTP/S → Save → Connect, confirm the OS default browser actually opens" line of the
/// manual click-test punch list (https://github.com/chris3081/PolyTerm/issues/1). Entirely
/// keyboard-driven (see <see cref="KeyboardUiActions"/>).
/// </summary>
/// <remarks>
/// HTTP/S profiles now embed by default (Phase 2 of #27 — <c>WebSession</c> exposes
/// <c>IProtocolSession.Web</c> and gets a real <c>SurfaceKind.WebView</c> tab, via
/// <c>PolyTerm.App.Surfaces.Web.WebSurface</c>) rather than always launching the OS default
/// browser. <see cref="ConnectingToAnHttpProfile_OpensAnEmbeddedWebViewTab"/> covers that default
/// path; <see cref="ConnectingToAnHttpProfile_WithOpenExternally_LaunchesTheDefaultBrowserInstead"/>
/// covers the per-profile "Open in external browser instead" opt-out, which still routes through
/// the original <c>SurfaceKind.External</c>/no-tab behavior these tests originally covered.
/// </remarks>
public sealed class HttpLauncherTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public HttpLauncherTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void ConnectingToAnHttpProfile_OpensAnEmbeddedWebViewTab()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "flaui-http-launcher-test";
        const string testUrl = "https://example.com";

        CreateHttpProfile(profileName, testUrl);
        ConnectViaCommandPalette(profileName);

        // AutomationId, not a text-based lookup - this suite already hit a real Avalonia-12 UIA
        // regression where ComboBox.SelectedItem reads back empty (see
        // KeyboardUiActions.GetComboBoxDisplayedText's own doc comment) - AutomationId lookups are
        // the reliable path. Generous timeout: cold WebView2 init plus a real page load can take a
        // few seconds, unlike the other surfaces' pure in-process rendering.
        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        AutomationElement? webSurface = null;
        var found = SpinWait.SpinUntil(() =>
        {
            webSurface = tabControl.FindFirstDescendant(cf => cf.ByAutomationId("WebSurface"));
            return webSurface is not null;
        }, TimeSpan.FromSeconds(15));

        found.Should().BeTrue("connecting to an HTTP/S profile should open a real embedded WebSurface tab by default");
        webSurface.Should().NotBeNull();
    }

    [Fact]
    public void ConnectingToAnHttpProfile_WithOpenExternally_LaunchesTheDefaultBrowserInstead()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "flaui-http-launcher-external-test";
        const string testUrl = "https://example.com";

        CreateHttpProfile(profileName, testUrl, openExternally: true);
        ConnectViaCommandPalette(profileName);

        // Which real process/window actually opens is up to whatever the OS's default browser
        // association resolves to (quite possibly just a new tab in an already-running browser, not
        // a new top-level window) - so the real, portable check here is structural: no PolyTerm-owned
        // dialog or tab is left open afterward (no error dialog blocked the flow, and — unlike the
        // embedded-by-default path above — no WebSurface tab was created either).
        var noOwnedWindow = KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow);
        noOwnedWindow.Should().BeTrue("openExternally should still route through the original external-browser path with no PolyTerm tab");
    }

    /// <summary>Drives the real New Connection dialog via keyboard to create one HTTP profile, optionally checking "Open in external browser instead" before saving.</summary>
    private void CreateHttpProfile(string profileName, string testUrl, bool openExternally = false)
    {
        var dialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: 0);

        // SSH is the default protocol - switch to HTTP/S via the Protocol ComboBox (real keyboard
        // selection, same technique already proven in JumpHostChainingTests: a closed ComboBox's
        // selection cycles directly on Up/Down, no dropdown-open/Enter-to-confirm step needed).
        // Protocols are sorted alphabetically by DisplayName (NewConnectionViewModel) and default to
        // "SSH" for a new connection - "HTTP"/"HTTPS" sort *before* "SSH" alphabetically, so reaching
        // them from the default selection means Up, not Down (ground-truthed directly: Down instead
        // walked SSH -> Telnet -> VNC, the end of the list, confirming HTTP lies the other direction).
        // Reads the current selection via KeyboardUiActions.GetComboBoxDisplayedText, not
        // ComboBox.SelectedItem directly - see that helper's own doc comment for why (a real Avalonia
        // 12 UIA-exposure regression confirmed while migrating off Avalonia 11.2.7, not a flake).
        var protocolCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ProtocolComboBox"))?.AsComboBox()
            ?? throw new InvalidOperationException("ProtocolComboBox not found.");
        protocolCombo.Focus();
        Thread.Sleep(200);
        var httpSelected = false;
        for (var i = 0; i < 10; i++)
        {
            var current = KeyboardUiActions.GetComboBoxDisplayedText(protocolCombo);
            if (current == "HTTP")
            {
                httpSelected = true;
                break;
            }
            Keyboard.Type(VirtualKeyShort.UP);
            Thread.Sleep(150);
        }
        httpSelected.Should().BeTrue("the Protocol ComboBox's plain HTTP option should be reachable via Up arrow from the default SSH selection");
        Console.WriteLine($"Selected protocol: {protocolCombo.SelectedItem?.Name}");

        var nameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionNameInput not found.");
        nameBox.Focus();
        Keyboard.Type(profileName);
        Thread.Sleep(150);

        var hostBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionHostInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionHostInput not found.");
        hostBox.Focus();
        Keyboard.Type(testUrl);
        Thread.Sleep(150);

        if (openExternally)
        {
            var checkbox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("OpenExternallyCheckBox"))?.AsCheckBox()
                ?? throw new InvalidOperationException("OpenExternallyCheckBox not found.");
            checkbox.Toggle();
            Thread.Sleep(150);
        }

        KeyboardUiActions.ActivateButtonByName(dialog, "Save");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("Save should close the New Connection dialog");
    }

    /// <summary>"Go to connection" via the Command Palette - the only reliable keyboard path to a saved profile's ConnectCommand (see SshAutoReconnectTests's own doc comment - the tree row's own connect gesture is a mouse-only double-tap with no keyboard equivalent).</summary>
    private void ConnectViaCommandPalette(string profileName)
    {
        var quickConnectInput = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("QuickConnectInput"))
            ?? throw new InvalidOperationException("QuickConnectInput not found.");
        quickConnectInput.Focus();
        Thread.Sleep(200);
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT))
        {
            Thread.Sleep(150);
            Keyboard.Type(VirtualKeyShort.KEY_P);
            Thread.Sleep(150);
        }
        Thread.Sleep(500);

        var paletteWindow = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        var searchBox = paletteWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPaletteSearchBox"))?.AsTextBox()
            ?? throw new InvalidOperationException("CommandPaletteSearchBox not found.");
        searchBox.Focus();
        Keyboard.Type(profileName);
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1500);
    }
}
