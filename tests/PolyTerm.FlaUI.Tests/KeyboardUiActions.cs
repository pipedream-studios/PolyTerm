using System.Linq;
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
    /// UIA focus is only used to reach the top-level menu itself, not the leaf item. When
    /// <paramref name="submenuDownPresses"/> is non-null, the item reached by <paramref name="downPresses"/>
    /// is expected to itself have a submenu (e.g. File &gt; New Connection) — <c>Right</c> opens it
    /// (landing the highlight on its first child, ground-truthed the same way top-level Down navigation
    /// was), then <c>Down</c> moves that many steps further before the final <c>Enter</c> executes.
    /// </summary>
    public static void ActivateTopMenuItem(Window mainWindow, string topMenuName, int downPresses, int? submenuDownPresses = null)
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

        if (submenuDownPresses is { } subDown)
        {
            Keyboard.Type(VirtualKeyShort.RIGHT);
            Thread.Sleep(300);
            for (var i = 0; i < subDown; i++)
            {
                Keyboard.Type(VirtualKeyShort.DOWN);
                Thread.Sleep(250);
            }
        }

        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(400);
    }

    /// <summary>
    /// Combines <see cref="ActivateTopMenuItem"/> with <see cref="WaitForOwnedWindow"/>, retrying the
    /// whole menu action (not just waiting longer for one attempt) if the resulting window doesn't
    /// appear within <paramref name="perAttemptTimeout"/>. Confirmed on real CI runs (windows-latest,
    /// "full solution" leg, which runs every other test project's real background threads/servers
    /// concurrently) that a single menu-activation attempt occasionally never produces its expected
    /// window at all, even given a generous single-attempt timeout - the same "keystroke sometimes
    /// doesn't land under contention" pattern independently found and fixed the same way in
    /// <see cref="CommandPaletteTests"/> and <see cref="McpApprovalGateTests"/>, factored out here once
    /// other call sites (every "File &gt; New Connection"-style dialog open in this suite) hit the same
    /// failure mode instead of patching each one individually.
    /// </summary>
    public static Window ActivateTopMenuItemAndWaitForWindow(Window mainWindow, string topMenuName, int downPresses,
        int maxAttempts = 3, TimeSpan? perAttemptTimeout = null, int? submenuDownPresses = null)
    {
        var timeout = perAttemptTimeout ?? TimeSpan.FromSeconds(10);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ActivateTopMenuItem(mainWindow, topMenuName, downPresses, submenuDownPresses);
            try
            {
                return WaitForOwnedWindow(mainWindow, timeout);
            }
            catch (TimeoutException) when (attempt < maxAttempts - 1)
            {
                // fall through and retry the menu action itself
            }
        }
        return WaitForOwnedWindow(mainWindow, timeout);
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
            Console.WriteLine($"ActivateButtonByName('{name}'): direct-Invoke path.");
            return;
        }
        catch (COMException ex)
        {
            Console.WriteLine($"ActivateButtonByName('{name}'): direct-Invoke path threw {ex.GetType().Name} ({ex.Message}), falling back to Tab-cycling.");
        }

        for (var i = 0; i < 15; i++)
        {
            var focused = root.FindAllDescendants().FirstOrDefault(el => el.Properties.HasKeyboardFocus.ValueOrDefault);
            if (focused?.Name == name)
            {
                Keyboard.Type(VirtualKeyShort.SPACE);
                Thread.Sleep(400);
                Console.WriteLine($"ActivateButtonByName('{name}'): Tab-cycling path, reached after {i + 1} Tab press(es).");
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
    ///
    /// When <paramref name="expectedTitle"/> is given, only a window with that exact title satisfies
    /// the wait — any *other* window present (there's a real global keyboard shortcut, Ctrl+Shift+K,
    /// for Quick Connect, and this suite's own tests fire a lot of rapid synthetic keystrokes/held
    /// modifiers) is skipped rather than returned, so this can't hand back the wrong dialog just
    /// because something else happened to be open at the same moment - confirmed as a real failure
    /// mode in CI (a "Quick Connect" window caught here instead of the caller's actual target).
    /// </summary>
    public static Window WaitForOwnedWindow(AutomationElement owner, TimeSpan timeout, string? expectedTitle = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var found = expectedTitle is null
                ? TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window))
                : TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window).And(cf.ByName(expectedTitle)));
            if (found is not null) return found.AsWindow();
            Thread.Sleep(300);
        }
        throw new TimeoutException(expectedTitle is null
            ? $"No owned window appeared under '{owner.Name}' within {timeout}."
            : $"No owned window titled '{expectedTitle}' appeared under '{owner.Name}' within {timeout}.");
    }

    /// <summary>True once no window remains as a descendant of <paramref name="owner"/> — used to
    /// confirm a dialog actually closed.</summary>
    public static bool NoOwnedWindowRemains(AutomationElement owner) =>
        TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window)) is null;

    /// <summary>
    /// True once no window titled <paramref name="title"/> remains as a descendant of
    /// <paramref name="owner"/> - unlike <see cref="NoOwnedWindowRemains"/>, tolerant of some other,
    /// unrelated window being open at the same time (see <see cref="WaitForOwnedWindow"/>'s own doc
    /// comment on why that's a real scenario here) rather than requiring the owner to have zero owned
    /// windows at all.
    /// </summary>
    public static bool NoOwnedWindowWithTitleRemains(AutomationElement owner, string title) =>
        TryFindFirst(owner, cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title))) is null;

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

    /// <summary>
    /// Finds a descendant by <paramref name="automationId"/>, retrying for up to
    /// <paramref name="timeout"/> (default 5s) rather than a single immediate lookup — covers the window-
    /// appeared-before-its-children-finished-rendering race: a dialog's <see cref="ControlType.Window"/>
    /// can land in the UIA tree a beat before Avalonia has finished laying out and exposing its content,
    /// so grabbing a control right after <see cref="ActivateTopMenuItemAndWaitForWindow"/> or
    /// <see cref="WaitForOwnedWindow"/> returns can occasionally miss it (confirmed in CI:
    /// "ProtocolComboBox not found" immediately after the New Connection dialog appeared).
    /// </summary>
    public static AutomationElement FindDescendantByAutomationId(AutomationElement root, string automationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        AutomationElement? found = null;
        while (DateTime.UtcNow < deadline)
        {
            found = TryFindFirst(root, cf => cf.ByAutomationId(automationId));
            if (found is not null) return found;
            Thread.Sleep(200);
        }
        throw new InvalidOperationException($"'{automationId}' not found under '{root.Name}' within {timeout ?? TimeSpan.FromSeconds(5)}.");
    }

    /// <summary>
    /// Returns a closed <see cref="ComboBox"/>'s currently-selected item as plain displayed text,
    /// reading it from the control's own descendant <see cref="ControlType.Text"/> element rather than
    /// <see cref="ComboBox.SelectedItem"/> — confirmed directly (see <c>HttpLauncherTests</c>'s own
    /// investigation) that under Avalonia 12, <c>SelectedItem</c> always reads back empty for this
    /// app's ComboBoxes even though the real, underlying selection changes correctly on every Up/Down
    /// keypress (verified by dumping this exact descendant text after each keypress and watching it
    /// cycle through every item in order). Same category of gap as the already-documented ListBox
    /// SelectionPattern issue (see <c>SftpPermissionsTests</c>) - Avalonia's UIA SelectionPattern/
    /// SelectionItemPattern exposure for its own selecting-items-controls remains incomplete, this
    /// project just hadn't needed to observe a ComboBox's selection this way until Avalonia 12 changed
    /// something that made the old <c>SelectedItem</c> read stop working (it worked under 11.2.7).
    /// </summary>
    public static string? GetComboBoxDisplayedText(ComboBox combo) =>
        combo.FindAllDescendants().FirstOrDefault(d => d.ControlType == ControlType.Text)?.Name;
}
