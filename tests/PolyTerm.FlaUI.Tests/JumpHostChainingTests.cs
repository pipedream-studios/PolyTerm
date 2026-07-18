using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using AwesomeAssertions;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the New Connection dialog's Jump Host picker — closing the
/// "Jump host / bastion chaining UI" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Entirely keyboard-driven (see
/// <see cref="KeyboardUiActions"/>). Saves two real profiles into the isolated, per-test database (see
/// <see cref="PolyTermAppFixture.OpenProfileStoreForVerification"/>) and verifies the jump-host
/// selection actually round-trips through the real storage layer, not just the in-memory UI state.
/// </summary>
public sealed class JumpHostChainingTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public JumpHostChainingTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SelectingAJumpHost_InTheNewConnectionDialog_PersistsToTheSavedProfile()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        CreateSshProfile("zz-jump-host", "203.0.113.10");
        CreateSshProfile("zz-real-profile", "203.0.113.20", selectFirstJumpHost: true);

        var store = _fixture.OpenProfileStoreForVerification();
        var all = await store.GetAllAsync();

        var jumpHostProfile = all.Should().ContainSingle(p => p.Name == "zz-jump-host").Subject;
        var realProfile = all.Should().ContainSingle(p => p.Name == "zz-real-profile").Subject;

        realProfile.Settings.GetJumpHostProfileId().Should().Be(jumpHostProfile.Id,
            "selecting 'zz-jump-host' in the Jump Host ComboBox and saving should persist its profile ID " +
            "on 'zz-real-profile', round-tripped through the real profile store");
    }

    /// <summary>Drives the real New Connection dialog via keyboard to create one SSH profile, optionally
    /// selecting the first non-"None" entry in the Jump Host ComboBox before saving.</summary>
    private void CreateSshProfile(string name, string host, bool selectFirstJumpHost = false)
    {
        var dialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: 0);

        var nameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionNameInput not found.");
        nameBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type(name);
        Thread.Sleep(150);

        var hostBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionHostInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionHostInput not found.");
        hostBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type(host);
        Thread.Sleep(150);

        if (selectFirstJumpHost)
        {
            // Jump Host moved to the Organization tab (issue #68) - it's not in the visual tree until
            // that tab's header button is clicked (see NewConnectionWindow.axaml's tab-switch comment).
            KeyboardUiActions.ActivateButtonByName(dialog, "Organization");
            var jumpHostCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("JumpHostComboBox"))?.AsComboBox()
                ?? throw new InvalidOperationException("JumpHostComboBox not found.");
            jumpHostCombo.Focus();
            Thread.Sleep(200);

            // A closed ComboBox's selection cycles directly on Up/Down (standard Avalonia/WPF
            // behavior - no dropdown-open/Enter-to-confirm step needed). "— None —" is index 0, so one
            // Down reaches the first real jump-host candidate (alphabetically first, and in this test
            // the only one: "zz-jump-host"). Polls rather than a single fixed-delay check - confirmed
            // this test is reliable locally but consistently failed on every CI run since it was
            // introduced (a slower/different UI-automation environment on the hosted runner), which a
            // bare Thread.Sleep(300) doesn't leave enough margin for.
            // Reads the current selection via KeyboardUiActions.GetComboBoxDisplayedText, not
            // ComboBox.SelectedItem directly - see that helper's own doc comment for why (a real
            // Avalonia 12 UIA-exposure regression confirmed while migrating off Avalonia 11.2.7).
            Keyboard.Type(VirtualKeyShort.DOWN);
            var selected = WaitFor(() => KeyboardUiActions.GetComboBoxDisplayedText(jumpHostCombo)?.Contains("zz-jump-host") == true, TimeSpan.FromSeconds(5));
            selected.Should().BeTrue(
                $"one Down from the default '— None —' selection should land on the only other saved SSH profile, but SelectedItem was '{KeyboardUiActions.GetComboBoxDisplayedText(jumpHostCombo)}'");
        }

        KeyboardUiActions.ActivateButtonByName(dialog, "Save");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue($"Save should close the New Connection dialog after creating '{name}'");
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(200);
        }
        return condition();
    }
}
