using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, verified keyboard-only techniques for driving PolyTerm's UI in FlaUI tests, discovered while
/// closing out https://github.com/chris3081/PolyTerm/issues/1. This repo has a confirmed, deeply
/// investigated gap where synthetic mouse clicks (SendInput, PostMessage, and even the UIA Invoke
/// pattern) never reach Avalonia's <c>Click</c> handler here (see <see cref="MainWindowTests"/>'s
/// skipped test) — but two purely keyboard-driven paths do reliably fire real <c>Click</c> handlers,
/// confirmed empirically (not assumed) with unambiguous side effects (a real local-shell process
/// starting, the whole app actually exiting on a menu "Exit" activation, a real dialog actually
/// closing):
/// <list type="bullet">
/// <item>Top-level menu navigation via real synthetic key events: UIA <c>.Focus()</c> on a top-level
/// <see cref="MenuItem"/> (e.g. "File") + <c>Enter</c> opens it, then real <c>Down</c> arrow key
/// presses (not UIA focus calls — those don't move the menu's own internal highlight) move the
/// highlight, and <c>Enter</c> executes whatever is currently highlighted.</item>
/// <item>Plain (non-menu) buttons: UIA <c>.Focus()</c> to give the control real keyboard focus, then a
/// synthetic <c>Space</c> keypress fires <c>Click</c> — unlike <see cref="MenuItem"/>, an ordinary
/// <see cref="Button"/> in a plain Window has no separate "highlighted vs. focused" split, so UIA focus
/// alone is sufficient here.</item>
/// </list>
/// A further gotcha applies to both: windows opened from within PolyTerm (New Connection, Tunnels,
/// Macros, the Command Palette, etc.) are reported by UIA as *descendants* of their owner window, not as
/// sibling top-level windows — <c>Application.GetAllTopLevelWindows</c> never finds them regardless of
/// how they were opened. Search the owner's own descendants instead (<see cref="WaitForOwnedWindow"/>).
/// </summary>
internal static class KeyboardUiActions
{
    /// <summary>
    /// Opens a top-level menu (e.g. "File") and executes the item <paramref name="downPresses"/> steps
    /// below the menu's default highlight (0 = the first item). Real end-to-end keyboard interaction:
    /// UIA focus is only used to reach the top-level menu itself, not the leaf item.
    /// </summary>
    public static void ActivateTopMenuItem(Window mainWindow, string topMenuName, int downPresses)
    {
        var topMenu = mainWindow.FindFirstDescendant(cf => cf.ByName(topMenuName))
            ?? throw new InvalidOperationException($"Top-level menu '{topMenuName}' not found.");
        topMenu.Focus();
        Thread.Sleep(350);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(400);

        for (var i = 0; i < downPresses; i++)
        {
            Keyboard.Type(VirtualKeyShort.DOWN);
            Thread.Sleep(250);
        }

        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(400);
    }

    /// <summary>Gives <paramref name="button"/> real keyboard focus via UIA, then fires its
    /// <c>Click</c> handler with a synthetic <c>Space</c> keypress.</summary>
    public static void ActivateButton(Button button)
    {
        button.Focus();
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.SPACE);
        Thread.Sleep(400);
    }

    /// <summary>
    /// Finds a <see cref="Button"/> by its accessible name under <paramref name="root"/> and activates
    /// it. Falls back to real <c>Tab</c>-key cycling (never UIA <c>SetFocus</c>) if direct UIA focus
    /// throws — confirmed real, not hypothetical: a non-modal window (<c>MacrosWindow.Show()</c>, unlike
    /// the <c>ShowDialog</c>-based windows the other punch-list tests open) consistently threw
    /// <c>DISP_E_MEMBERNOTFOUND</c> from <c>IUIAutomationElement.SetFocus()</c> for its buttons — every
    /// other UIA signal checked (foreground state, bounding rect, IsEnabled/IsOffscreen) was correct, so
    /// this looks like a real gap in Avalonia's automation-peer wiring for non-modal windows specifically,
    /// not a timing fluke. Genuine synthetic Tab keystrokes (which cycle the window's own focus manager
    /// rather than calling the COM method that's failing) reliably reach the same button instead.
    /// </summary>
    public static void ActivateButtonByName(AutomationElement root, string name)
    {
        var button = root.FindFirstDescendant(cf => cf.ByName(name))?.AsButton()
            ?? throw new InvalidOperationException($"Button '{name}' not found.");
        try
        {
            ActivateButton(button);
            return;
        }
        catch (COMException)
        {
            // Fall through to Tab-cycling below.
        }

        for (var i = 0; i < 15; i++)
        {
            var focused = root.FindAllDescendants().FirstOrDefault(el => el.Properties.HasKeyboardFocus.ValueOrDefault);
            if (focused?.Name == name)
            {
                Keyboard.Type(VirtualKeyShort.SPACE);
                Thread.Sleep(400);
                return;
            }
            Keyboard.Type(VirtualKeyShort.TAB);
            Thread.Sleep(200);
        }
        throw new InvalidOperationException($"Could not reach button '{name}' via Tab-key cycling after 15 attempts.");
    }

    /// <summary>
    /// Waits for a window PolyTerm opened as a descendant of <paramref name="owner"/> to appear in the
    /// UIA tree (see this class's own doc comment for why it isn't a top-level sibling window) and
    /// returns it. Uses <c>FindFirstDescendant</c> rather than restricting to direct children — a first
    /// attempt at direct-children-only search never found the window at all, confirming owned dialogs
    /// aren't necessarily reported as immediate UIA children of their owner.
    /// </summary>
    public static Window WaitForOwnedWindow(AutomationElement owner, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var found = TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window));
            if (found is not null) return found.AsWindow();
            Thread.Sleep(300);
        }
        throw new TimeoutException($"No owned window appeared under '{owner.Name}' within {timeout}.");
    }

    /// <summary>True once no window remains as a descendant of <paramref name="owner"/> — used to
    /// confirm a dialog actually closed.</summary>
    public static bool NoOwnedWindowRemains(AutomationElement owner) =>
        TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window)) is null;

    /// <summary>
    /// UI Automation's underlying COM calls occasionally time out transiently, independent of anything
    /// the test does — confirmed directly in this project (see <c>CommandPaletteTests</c>'s own doc
    /// comment: an early version crashed outright on the first polling attempt with a raw
    /// <see cref="COMException"/> 0x80131505). Swallow and retry on the next poll tick instead of
    /// failing the whole test on one transient timeout.
    /// </summary>
    public static AutomationElement? TryFindFirst(AutomationElement root, Func<ConditionFactory, ConditionBase> condition)
    {
        try { return root.FindFirstDescendant(condition); }
        catch (COMException) { return null; }
    }
}
