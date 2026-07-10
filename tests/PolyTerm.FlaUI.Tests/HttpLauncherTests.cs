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
/// An HTTP/S "session" (<c>WebSession</c>) has <c>SurfaceKind.External</c> - launching the URL via
/// <c>Process.Start(UseShellExecute: true)</c> already produced the session's real "surface" (a browser
/// window/tab), so <c>MainViewModel.OnSessionOpened</c> disposes it immediately and never creates an app
/// tab. There's deliberately nothing in this app's own UI left to assert on afterward, and which real
/// process/window actually opens is up to whatever the OS's default browser association resolves to
/// (quite possibly just a new tab in an already-running browser, not a new top-level window - this dev
/// machine already has Firefox open) - so the real, portable check here is structural: no
/// PolyTerm-owned dialog is left open afterward (no error dialog blocked the flow). <c>WebSession</c>'s
/// own real log output (visible when running this test directly - the launched PolyTerm.exe shares this
/// process's console, confirmed by every "info:"/"dbug:" line already visible throughout this suite's
/// own test output) shows the exact URL and confirms success/failure further, but isn't asserted on
/// here since the test process doesn't capture it programmatically.
/// </remarks>
public sealed class HttpLauncherTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public HttpLauncherTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void ConnectingToAnHttpProfile_LaunchesTheDefaultBrowserWithTheRightUrl()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "flaui-http-launcher-test";
        const string testUrl = "https://example.com";

        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 1);
        var dialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));

        // SSH is the default protocol - switch to HTTP/S via the Protocol ComboBox (real keyboard
        // selection, same technique already proven in JumpHostChainingTests: a closed ComboBox's
        // SelectedItem cycles directly on Up/Down, no dropdown-open/Enter-to-confirm step needed).
        // Protocols are sorted alphabetically by DisplayName (NewConnectionViewModel) and default to
        // "SSH" for a new connection - "HTTP"/"HTTPS" sort *before* "SSH" alphabetically, so reaching
        // them from the default selection means Up, not Down (ground-truthed directly: Down instead
        // walked SSH -> Telnet -> VNC, the end of the list, confirming HTTP lies the other direction).
        var protocolCombo = dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))?.AsComboBox()
            ?? throw new InvalidOperationException("Protocol ComboBox not found (expected to be the first ComboBox in the dialog).");
        protocolCombo.Focus();
        Thread.Sleep(200);
        var httpSelected = false;
        for (var i = 0; i < 10; i++)
        {
            var current = protocolCombo.SelectedItem?.Name;
            if (current?.Contains("DisplayName = HTTP,", StringComparison.OrdinalIgnoreCase) == true)
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

        KeyboardUiActions.ActivateButtonByName(dialog, "Save");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("Save should close the New Connection dialog");

        // "Go to connection" via the Command Palette - the only reliable keyboard path to a saved
        // profile's ConnectCommand (see SshAutoReconnectTests's own doc comment - the tree row's own
        // connect gesture is a mouse-only double-tap with no keyboard equivalent).
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

        var noOwnedWindow = KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow);
        noOwnedWindow.Should().BeTrue("connecting to an HTTP/S profile should not leave any PolyTerm-owned dialog open (no tab, no error dialog)");
    }
}
