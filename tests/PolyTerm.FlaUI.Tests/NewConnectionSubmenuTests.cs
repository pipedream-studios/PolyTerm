using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end coverage of File &gt; New Connection's per-protocol submenu entries — each one
/// should open the New Connection dialog with its own protocol already selected, not just the dialog's
/// own SSH default (see <c>MainWindow.NewConnection_Click</c>'s <c>Tag</c>-based mechanism).
/// Covers two of the newly added entries (RDP, VNC) as real ground-truthed submenu-index checks; the
/// SSH/Local Shell entries at indices 0/1 are already covered end-to-end by every other test in this
/// suite that opens the New Connection dialog via <c>submenuDownPresses: 0</c>.
/// </summary>
public sealed class NewConnectionSubmenuTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public NewConnectionSubmenuTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void NewConnection_Rdp_OpensTheDialogWithRdpAlreadySelected()
    {
        AssertSubmenuEntryPreselects(submenuDownPresses: 2, expectedProtocol: "RDP");
    }

    [Fact]
    public void NewConnection_Vnc_OpensTheDialogWithVncAlreadySelected()
    {
        AssertSubmenuEntryPreselects(submenuDownPresses: 3, expectedProtocol: "VNC");
    }

    /// <summary>
    /// Drives File &gt; New Connection &gt; (submenu index <paramref name="submenuDownPresses"/> below
    /// "SSH…") and asserts the Protocol ComboBox already shows <paramref name="expectedProtocol"/> with
    /// no manual switching — reads via <see cref="KeyboardUiActions.GetComboBoxDisplayedText"/>, not
    /// <c>ComboBox.SelectedItem</c> directly (a real Avalonia 12 UIA-exposure regression, see that
    /// helper's own doc comment), then cancels rather than saving a profile.
    /// </summary>
    private void AssertSubmenuEntryPreselects(int submenuDownPresses, string expectedProtocol)
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        var dialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: submenuDownPresses);

        var protocolCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ProtocolComboBox"))?.AsComboBox()
            ?? throw new InvalidOperationException("ProtocolComboBox not found.");
        var selected = KeyboardUiActions.GetComboBoxDisplayedText(protocolCombo);
        selected.Should().Be(expectedProtocol,
            $"the File > New Connection submenu entry should pre-select {expectedProtocol} without any manual ComboBox switching");

        KeyboardUiActions.ActivateButtonByName(dialog, "Cancel");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("Cancel should close the New Connection dialog without saving a profile");
    }
}
