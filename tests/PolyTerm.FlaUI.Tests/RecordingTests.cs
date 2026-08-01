using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the recording consent dialog, the Save dialog, and the
/// pause/resume/stop badge flow — closing the "Recording pause/resume … and the 'Start Recording?'
/// consent dialog" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Entirely keyboard-driven (see
/// <see cref="KeyboardUiActions"/>), including the Save dialog <c>StartRecording_Click</c> opens via
/// <c>StorageProvider.SaveFilePickerAsync</c> - a real dialog, but reported by UIA as a descendant of
/// MainWindow same as every other PolyTerm-owned window, not a separate desktop-level one.
/// </summary>
/// <remarks>
/// Deliberately does not assert on captured terminal *content* (e.g. typing a marker command and
/// checking the .cast file for it) - the recording-specific behavior this test covers (consent dialog,
/// Save dialog, pause/resume/stop badges) doesn't depend on what the shell actually outputs, only on
/// whether recording is active. Issue #23 (a local-shell tab producing zero terminal output at all) was
/// root-caused and fixed separately - see ConPtyTerminal.cs.
/// </remarks>
public sealed class RecordingTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public RecordingTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void ConsentDialog_ThenSaveDialog_ThenPauseResumeStop_ProducesARealFile()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // File > New Connection > Local Shell (ground-truthed: Down#1 to "New Connection", Right to
        // open its submenu landing on "SSH…", Down#1 more to "Local Shell"). Retries the whole menu
        // action itself (not just waiting longer for one attempt) if a tab doesn't appear within a few
        // seconds - the same "keystroke sometimes doesn't land under contention" pattern already found
        // and fixed the same way in McpApprovalGateTests.OpenNewLocalShellTab and
        // KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow, which this bare ActivateTopMenuItem
        // call had never picked up.
        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        AutomationElement? shellTab = null;
        for (var attempt = 0; attempt < 3 && shellTab is null; attempt++)
        {
            KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: 1);
            // Match by ControlType, not by the tab's displayed text ("Local Shell") - the tab title
            // reflects SessionTabViewModel.Title, which a real, live shell overrides via its own OSC
            // 0/2 title sequence once ConPTY relays it (confirmed: a freshly-started cmd.exe/powershell.exe
            // here immediately retitles its tab to its own executable path). Only reproduces once the
            // shell process actually survives long enough to send that sequence - see issue #23/#2's
            // ConPTY fix.
            shellTab = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByControlType(ControlType.TabItem)),
                TimeSpan.FromSeconds(10));
        }
        shellTab.Should().NotBeNull("New Local Shell should have opened a real tab");

        // Session > Start Recording… (first item, auto-highlighted - 0 Downs).
        var consentDialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "Session", downPresses: 0);
        consentDialog.Title.Should().Be("Start Recording?");
        KeyboardUiActions.ActivateButtonByName(consentDialog, "Start Recording");

        var recordingDir = Path.Combine(Path.GetTempPath(), "PolyTermFlaUITests");
        Directory.CreateDirectory(recordingDir);
        var recordingPath = Path.Combine(recordingDir, $"recording-test-{Guid.NewGuid():N}.cast");
        SaveViaNativeFileDialog(recordingPath);

        // Recording is now active - the tab badge only appears once IsRecordingActive flips, which
        // happens synchronously in StartRecording_Click after the file picker returns.
        var recordingBadge = WaitFor(
            () => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByAutomationId("RecordingActiveBadge")),
            TimeSpan.FromSeconds(10));
        recordingBadge.Should().NotBeNull("the tab should show the recording-active badge once recording starts");
        File.Exists(recordingPath).Should().BeTrue("Start Recording should create the real file immediately");

        // Session > Pause Recording (Down#1 - ground-truthed: Start(0), Pause-or-Resume(1), Stop(2),
        // consistent regardless of which of Pause/Resume is currently visible).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 1);
        var pausedBadge = WaitFor(
            () => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByAutomationId("RecordingPausedBadge")),
            TimeSpan.FromSeconds(10));
        pausedBadge.Should().NotBeNull("the tab badge should switch to the paused badge after pausing");

        // Session > Resume Recording (same Down#1 position).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 1);
        var resumedBadge = WaitFor(
            () => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByAutomationId("RecordingActiveBadge")),
            TimeSpan.FromSeconds(10));
        resumedBadge.Should().NotBeNull("the tab badge should switch back to the active badge after resuming");

        // Session > Stop Recording (Down#2).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 2);
        Thread.Sleep(500);

        var stoppedBadge = KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByAutomationId("RecordingActiveBadge"));
        stoppedBadge.Should().BeNull("the recording-active badge should disappear once recording is stopped");
        File.Exists(recordingPath).Should().BeTrue("Stop Recording should leave the real file on disk at the chosen path");
    }

    /// <summary>
    /// Types the full target path into the Save dialog's filename field and confirms. Despite being the
    /// real dialog behind <c>StorageProvider.SaveFilePickerAsync</c>, it's reported by UIA as a
    /// descendant of MainWindow, same as every other PolyTerm-owned dialog - not a separate top-level
    /// desktop window (confirmed empirically: an earlier version of this test searched the whole
    /// desktop via <c>Automation.GetDesktop()</c> and found nothing there, then found it immediately
    /// via the same owned-window search used everywhere else in this suite).
    /// </summary>
    private void SaveViaNativeFileDialog(string fullPath)
    {
        // Matched by title, not just "any owned window" - PolyTerm has a real global Ctrl+Shift+K
        // shortcut (Quick Connect) that this suite's own rapid synthetic keystrokes/held modifiers can
        // occasionally trigger, and a stray Quick Connect window grabbed here instead of the real Save
        // dialog was a confirmed CI failure mode (wrong title asserted below).
        var saveDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(15), expectedTitle: "Save Recording");
        saveDialog.Title.Should().Be("Save Recording");

        var filenameField = saveDialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))?.AsComboBox()
            ?? throw new InvalidOperationException("Could not find the Save dialog's filename field.");
        filenameField.Focus();
        Thread.Sleep(200);

        // Select-all then type, rather than assuming the field starts empty - the dialog pre-fills it
        // with the SuggestedFileName.
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) { Keyboard.Type(VirtualKeyShort.KEY_A); }
        Keyboard.Type(fullPath);
        Thread.Sleep(300);

        KeyboardUiActions.ActivateButtonByName(saveDialog, "Save");
        // 20s, not 10s: issue #79 saw this miss its window once under contention even though the click
        // itself (confirmed via KeyboardUiActions' own path logging) had already landed - a shared CI
        // runner has more contention than a local dev box, same reasoning FakeRfbServer's settle-delay
        // comment already applies elsewhere in this suite.
        //
        // Checked by title, not "zero owned windows total": if a stray Quick Connect window (see the
        // comment above on WaitForOwnedWindow) is open at the same time, it has nothing to do with this
        // Save dialog closing, but it would keep NoOwnedWindowRemains() false forever and time this out
        // - a second confirmed CI failure mode from the same root cause.
        var saveDialogClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowWithTitleRemains(_fixture.MainWindow, "Save Recording"), TimeSpan.FromSeconds(20));
        saveDialogClosed.Should().BeTrue("clicking Save in the Save dialog should close it");
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
