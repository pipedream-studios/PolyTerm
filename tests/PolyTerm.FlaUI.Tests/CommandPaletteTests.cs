using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the command palette's open/filter/navigate/run flow
/// (Ctrl+Shift+P → type → Enter) against the actual, already-built <c>PolyTerm.exe</c> — closing the
/// "command palette" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1) for real, not just leaving it as a manual gap.
/// Deliberately entirely keyboard-driven: this repo has a confirmed, deeply-investigated gap where
/// synthetic mouse clicks (SendInput and PostMessage alike) never reach Avalonia's Click handler here
/// (see <see cref="MainWindowTests"/>'s skipped test), so a test exercising the palette's own
/// <em>run</em> step has to pick a command reachable without ever clicking anything — "New Local Shell"
/// fits: it needs no external server, no dialog beyond the palette itself, and its effect (a new tab)
/// is trivially observable via UIA.
/// </summary>
public sealed class CommandPaletteTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;

    public CommandPaletteTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void OpenFilterAndRun_NewLocalShell_OpensARealNewTab()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var tabsBefore = TryFindDescendant(tabControl, cf => cf.ByText("Local Shell")) is null;
        tabsBefore.Should().BeTrue("no 'Local Shell' tab should exist yet at the start of this test");

        // MainWindow.OnKeyDown only sees this key combo if *something* inside the window already has
        // real input focus — bringing the window itself to the foreground isn't enough. Focusing a
        // known control via UIA first (same technique KeyboardDiagnosticTests already relies on)
        // establishes that without needing a mouse click.
        var quickConnectInput = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("QuickConnectInput"))
            ?? throw new InvalidOperationException("QuickConnectInput not found by AutomationId.");
        quickConnectInput.Focus();
        Thread.Sleep(200);

        // Ctrl+Shift+P — the same global shortcut MainWindow.OnKeyDown wires up. Deliberately holding
        // the modifiers down with a pause before/after the actual key press rather than
        // Keyboard.TypeSimultaneously (which presses/releases all three in immediate succession and,
        // confirmed by direct debugging, didn't reliably register): the app-level KeyDown handler
        // does receive the combo correctly this way (verified with a temporary debug log during
        // development — Key=P, Modifiers=Control|Shift, exactly matching MainWindow's own guard clause).
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT))
        {
            Thread.Sleep(150);
            Keyboard.Type(VirtualKeyShort.KEY_P);
            Thread.Sleep(150);
        }
        Thread.Sleep(500);

        // The palette opens fine (confirmed during development), but — unlike the "New Connection"
        // window MainWindowTests' skipped test looked for — it's reported as a *descendant* of
        // MainWindow in the UIA tree, not a sibling top-level desktop window, so
        // Application.GetAllTopLevelWindows never finds it. Search MainWindow's own descendants instead.
        AutomationElement? paletteElement = null;
        var openDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < openDeadline)
        {
            paletteElement = TryFindDescendant(_fixture.MainWindow,
                cf => cf.ByControlType(ControlType.Window).And(cf.ByName("Command Palette")));
            if (paletteElement is not null) break;
            Thread.Sleep(200);
        }
        paletteElement.Should().NotBeNull("Ctrl+Shift+P should open the command palette window");
        var paletteWindow = paletteElement!.AsWindow();

        var searchBox = paletteWindow.FindFirstDescendant(cf => cf.ByAutomationId("CommandPaletteSearchBox"))?.AsTextBox()
            ?? throw new InvalidOperationException("CommandPaletteSearchBox not found by AutomationId.");
        searchBox.Focus();
        Thread.Sleep(200);

        // Narrow enough that "New Local Shell" is the only/top match, matching how a real user filters.
        Keyboard.Type("local shell");
        Thread.Sleep(300);
        searchBox.Text.Should().Contain("local shell", "typed filter text should land in the palette's search box");

        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(500);

        var closeDeadline = DateTime.UtcNow.AddSeconds(10);
        var stillOpen = true;
        while (DateTime.UtcNow < closeDeadline)
        {
            stillOpen = TryFindDescendant(_fixture.MainWindow,
                cf => cf.ByControlType(ControlType.Window).And(cf.ByName("Command Palette"))) is not null;
            if (!stillOpen) break;
            Thread.Sleep(200);
        }
        stillOpen.Should().BeFalse("Enter should confirm the selection and close the palette");

        AutomationElement? newTab = null;
        var runDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < runDeadline)
        {
            newTab = TryFindDescendant(tabControl, cf => cf.ByText("Local Shell"));
            if (newTab is not null) break;
            Thread.Sleep(200);
        }
        newTab.Should().NotBeNull("running 'New Local Shell' from the palette should have opened a real new tab");
    }

    /// <summary>
    /// UI Automation's underlying COM calls (<c>IUIAutomationElement.FindAll</c> etc.) are documented to
    /// occasionally time out transiently under normal conditions, independent of anything this test does
    /// — confirmed directly: an early version of this test crashed outright on the very first polling
    /// attempt with a raw <see cref="COMException"/> (0x80131505). Retrying on the next poll tick instead
    /// of failing the whole test on one transient timeout is the correct fix, not a symptom to chase.
    /// </summary>
    private static AutomationElement? TryFindDescendant(AutomationElement root, Func<ConditionFactory, ConditionBase> condition)
    {
        try { return root.FindFirstDescendant(condition); }
        catch (COMException) { return null; }
    }
}
