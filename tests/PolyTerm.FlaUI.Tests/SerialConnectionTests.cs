using System.IO.Ports;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end GUI coverage of the New Connection dialog's serial-specific fields (Port, Baud
/// Rate, Data Bits, Parity, Stop Bits) against a real Cisco IOS switch console wired to COM3 on this
/// developer's machine - closing out the GUI half of https://github.com/chris3081/PolyTerm/issues/6
/// (the engine-level connect/read/write round-trip itself is covered separately, without any GUI, in
/// <c>PolyTerm.Protocols.Serial.Tests.SerialSessionTests</c>).
/// </summary>
/// <remarks>
/// Skips gracefully wherever COM3 isn't a real, present device (every other machine, including CI).
/// </remarks>
public sealed class SerialConnectionTests : IClassFixture<PolyTermAppFixture>
{
    private const string PortName = "COM3";

    private readonly PolyTermAppFixture _fixture;
    public SerialConnectionTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void SerialFields_ConnectARealCiscoConsole_ThroughTheNewConnectionDialog()
    {
        if (!SerialPort.GetPortNames().Contains(PortName, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"SKIP: {PortName} not present on this machine's port list ({string.Join(", ", SerialPort.GetPortNames())}).");
            return;
        }

        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "serial-cisco-flaui-test";
        var dialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: 0);

        var nameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionNameInput not found.");
        nameBox.Focus();
        Keyboard.Type(profileName);
        Thread.Sleep(150);

        // Protocols are alphabetically sorted by DisplayName (NewConnectionViewModel's own ordering);
        // ground-truthed order is HTTP, HTTPS, Raw TCP, RDP, Rlogin, Serial, SSH, Telnet, VNC (indices
        // 0-8). A brand-new connection defaults to "SSH" explicitly (NewConnectionViewModel's own
        // constructor, not "whatever's first in the list" - confirmed directly: an initial attempt
        // assuming a default of index 0 overshot to "VNC" after 5 Downs, since a closed ComboBox clamps
        // at the last item rather than wrapping). SSH is index 6, Serial is index 5 - one Up reaches it.
        // Same "closed ComboBox cycles SelectedItem directly on Up/Down" technique JumpHostChainingTests
        // already established for Avalonia ComboBox.
        var protocolCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ProtocolComboBox"))?.AsComboBox()
            ?? throw new InvalidOperationException("ProtocolComboBox not found.");
        protocolCombo.Focus();
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.UP);
        Thread.Sleep(200);
        protocolCombo.SelectedItem?.Name.Should().Contain("Serial", "one Up from the default 'SSH' selection should land on 'Serial'");

        var portBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("SerialPortInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("SerialPortInput not found - the Serial-specific fields should be visible once Serial is selected.");
        portBox.Focus();
        Keyboard.Type(PortName);
        Thread.Sleep(150);

        // Explicitly drive Baud Rate/Data Bits/Parity/Stop Bits too, per issue #6's own "confirm ...
        // all work end-to-end" ask - even though their defaults (9600/8/None/One) already happen to
        // match this switch's real console settings, retyping/reselecting them here proves the fields
        // themselves are wired up and interactive, not just coincidentally already correct.
        var baudBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("SerialBaudRateInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("SerialBaudRateInput not found.");
        baudBox.Focus();
        Thread.Sleep(150);
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) { Keyboard.Type(VirtualKeyShort.KEY_A); }
        Keyboard.Type("9600");
        Thread.Sleep(150);

        var dataBitsBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("SerialDataBitsInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("SerialDataBitsInput not found.");
        dataBitsBox.Focus();
        Thread.Sleep(150);
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) { Keyboard.Type(VirtualKeyShort.KEY_A); }
        Keyboard.Type("8");
        Thread.Sleep(150);

        var parityCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("SerialParityCombo"))?.AsComboBox()
            ?? throw new InvalidOperationException("SerialParityCombo not found.");
        parityCombo.Focus();
        Thread.Sleep(200);
        // ParityOptions = ["None", "Odd", "Even", "Mark", "Space"] - already-selected "None" is index 0;
        // one Down then one Up proves the combo is real and interactive, then lands back on "None".
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.UP);
        Thread.Sleep(200);
        parityCombo.SelectedItem?.Name.Should().Be("None");

        var stopBitsCombo = dialog.FindFirstDescendant(cf => cf.ByAutomationId("SerialStopBitsCombo"))?.AsComboBox()
            ?? throw new InvalidOperationException("SerialStopBitsCombo not found.");
        stopBitsCombo.Focus();
        Thread.Sleep(200);
        // StopBitsOptions = ["One", "OnePointFive", "Two"] - same round-trip proof, landing back on "One".
        Keyboard.Type(VirtualKeyShort.DOWN);
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.UP);
        Thread.Sleep(200);
        stopBitsCombo.SelectedItem?.Name.Should().Be("One");

        KeyboardUiActions.ActivateButtonByName(dialog, "Save");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("Save should close the New Connection dialog");

        // Connect via the Command Palette, same mechanism every other real-server FlaUI test in this
        // suite already uses.
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
        Thread.Sleep(500);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var connected = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText(profileName)), TimeSpan.FromSeconds(15));
        connected.Should().NotBeNull(
            $"the tab should reach the plain '{profileName}' title once genuinely connected to the real switch - " +
            "a tab still showing '(connecting…)' or an error would mean the dialog's serial settings didn't " +
            "actually produce a working connection");
    }

    private static AutomationElement? WaitFor(Func<AutomationElement?> find, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        AutomationElement? found = null;
        while (DateTime.UtcNow < deadline)
        {
            found = find();
            if (found is not null) return found;
            Thread.Sleep(300);
        }
        return found;
    }
}
