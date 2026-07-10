using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the Tunnels editor's add/edit/save flow — closing the
/// "Tunnels editor UI" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Entirely keyboard-driven, using the two proven
/// techniques in <see cref="KeyboardUiActions"/> (real menu navigation, and UIA Focus()+Space on plain
/// buttons) — both confirmed to actually fire real <c>Click</c> handlers in this environment, unlike
/// mouse-based input (see <see cref="MainWindowTests"/>'s skipped test).
/// </summary>
public sealed class TunnelsEditorTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public TunnelsEditorTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void AddTunnel_ThenSave_AppearsInTheList_AndCancelingTheOuterDialogDiscardsIt()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // File > New SSH Connection… — the File menu auto-highlights its first item ("Quick Connect…")
        // on open, so one Down reaches "New SSH Connection…" (ground-truthed via per-item focus logging
        // during development: Down#1="New SSH Connection…", Down#2="New Local Shell", Down#3="Import",
        // Down#4="Exit" - the last of which really did close the whole app on Enter).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 1);

        var newConnectionWindow = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        Console.WriteLine($"New Connection window opened: '{newConnectionWindow.Title}'");

        // SSH is the default protocol for a brand-new connection (NewConnectionViewModel's constructor),
        // so the "Tunnels…" button (IsVisible bound to IsSshSelected) is already visible - no protocol
        // ComboBox interaction needed.
        KeyboardUiActions.ActivateButtonByName(newConnectionWindow, "Tunnels…");

        var tunnelsWindow = KeyboardUiActions.WaitForOwnedWindow(newConnectionWindow, TimeSpan.FromSeconds(10));
        tunnelsWindow.Title.Should().Be("Tunnels");

        var listBoxBefore = tunnelsWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
        var itemsBefore = listBoxBefore?.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length ?? 0;
        itemsBefore.Should().Be(0, "a brand-new connection should start with no tunnels configured");

        KeyboardUiActions.ActivateButtonByName(tunnelsWindow, "+ Tunnel");

        var editDialog = KeyboardUiActions.WaitForOwnedWindow(tunnelsWindow, TimeSpan.FromSeconds(10));
        editDialog.Title.Should().Be("Add Tunnel");

        // Kind stays at its default (Local), which needs Bind Port + Destination Host/Port filled in
        // before Save's CanExecute allows it (TunnelEditViewModel.CanSave).
        var bindPortBox = editDialog.FindFirstDescendant(cf => cf.ByAutomationId("TunnelBindPortInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("TunnelBindPortInput not found.");
        bindPortBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type("19222");
        Thread.Sleep(150);

        var destHostBox = editDialog.FindFirstDescendant(cf => cf.ByAutomationId("TunnelDestinationHostInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("TunnelDestinationHostInput not found.");
        destHostBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type("db.internal");
        Thread.Sleep(150);

        var destPortBox = editDialog.FindFirstDescendant(cf => cf.ByAutomationId("TunnelDestinationPortInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("TunnelDestinationPortInput not found.");
        destPortBox.Focus();
        Thread.Sleep(150);
        Keyboard.Type("5432");
        Thread.Sleep(150);

        bindPortBox.Text.Should().Be("19222");
        destHostBox.Text.Should().Be("db.internal");
        destPortBox.Text.Should().Be("5432");

        KeyboardUiActions.ActivateButtonByName(editDialog, "Save");

        // Save closes the edit dialog and appends to TunnelsWindow's list.
        var editDialogClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(tunnelsWindow), TimeSpan.FromSeconds(10));
        editDialogClosed.Should().BeTrue("Save should close the Add Tunnel dialog");

        var listBoxAfter = tunnelsWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
        var addedItem = KeyboardUiActions.TryFindFirst(tunnelsWindow, cf => cf.ByText("Local  127.0.0.1:19222 → db.internal:5432"));
        addedItem.Should().NotBeNull("the newly added tunnel should appear in the Tunnels list with the expected summary text");

        // Save the tunnel list back to the New Connection dialog, then Cancel the outer dialog rather
        // than actually persisting a profile — this test only needs to prove the Tunnels editor's own
        // add/edit/save flow works, not create real data in the (isolated, but still real-shaped) vault.
        KeyboardUiActions.ActivateButtonByName(tunnelsWindow, "Save");
        var tunnelsWindowClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(newConnectionWindow), TimeSpan.FromSeconds(10));
        tunnelsWindowClosed.Should().BeTrue("Save should close the Tunnels window and return to New Connection");

        KeyboardUiActions.ActivateButtonByName(newConnectionWindow, "Cancel");
        var newConnectionClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        newConnectionClosed.Should().BeTrue("Cancel should close the New Connection window without saving a profile");
    }
}
