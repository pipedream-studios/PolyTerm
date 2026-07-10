using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the Macros window's add/save flow — closing the
/// "Macros & snippets CRUD" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Entirely keyboard-driven (see
/// <see cref="KeyboardUiActions"/>), including its Tab-key fallback for
/// <c>MacrosWindow</c> specifically — confirmed necessary, not precautionary: direct UIA focus on this
/// non-modal (<c>Show()</c>, not <c>ShowDialog()</c>) window's buttons reliably threw
/// <c>DISP_E_MEMBERNOTFOUND</c> even with the window genuinely OS-foreground and the button's own UIA
/// properties (bounds, enabled, on-screen) all correct.
/// </summary>
public sealed class MacrosCrudTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public MacrosCrudTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void AddMacro_ThenSave_AppearsInTheMacrosList()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // The "_Macros" top menu has exactly one item ("_Manage Macros…"), auto-highlighted by default
        // when the menu opens - 0 Downs, straight to Enter (matches the ground-truthed File-menu
        // behavior: the first item is always pre-highlighted).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Macros", downPresses: 0);

        var macrosWindow = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        macrosWindow.Title.Should().Be("Macros");
        Win32ForceForeground.Apply(macrosWindow.Properties.NativeWindowHandle.Value);
        Thread.Sleep(300);

        var listBoxBefore = macrosWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
        var itemsBefore = listBoxBefore?.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length ?? 0;
        itemsBefore.Should().Be(0, "a fresh, isolated macros.json should start with no macros configured");

        KeyboardUiActions.ActivateButtonByName(macrosWindow, "+ Macro");

        var editDialog = KeyboardUiActions.WaitForOwnedWindow(macrosWindow, TimeSpan.FromSeconds(10));
        editDialog.Title.Should().Be("Add Macro");

        var nameBox = editDialog.FindFirstDescendant(cf => cf.ByAutomationId("MacroNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("MacroNameInput not found.");
        nameBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type("show-uptime");
        Thread.Sleep(150);

        var bodyBox = editDialog.FindFirstDescendant(cf => cf.ByAutomationId("MacroBodyInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("MacroBodyInput not found.");
        bodyBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type("uptime");
        Thread.Sleep(150);

        nameBox.Text.Should().Be("show-uptime");
        bodyBox.Text.Should().Be("uptime");

        KeyboardUiActions.ActivateButtonByName(editDialog, "Save");

        var editDialogClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(macrosWindow), TimeSpan.FromSeconds(10));
        editDialogClosed.Should().BeTrue("Save should close the Add Macro dialog");

        var addedName = KeyboardUiActions.TryFindFirst(macrosWindow, cf => cf.ByText("show-uptime"));
        addedName.Should().NotBeNull("the newly added macro should appear in the Macros list by name");
        var addedBody = KeyboardUiActions.TryFindFirst(macrosWindow, cf => cf.ByText("uptime"));
        addedBody.Should().NotBeNull("the newly added macro's body should also be shown in the list");

        KeyboardUiActions.ActivateButtonByName(macrosWindow, "Close");
        var macrosClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        macrosClosed.Should().BeTrue("Close should close the Macros window");
    }
}
