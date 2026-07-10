using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
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
/// checking the .cast file for it) - building this test surfaced a real, separate bug (issue #23):
/// a brand-new local-shell tab produces zero terminal output at all in this environment, confirmed via
/// screenshot evidence (blank after 3+ seconds and an Enter keystroke) and via the recording file
/// itself (only ever the asciicast header, no output events). That's a genuine gap in local-shell
/// output delivery, unrelated to recording specifically, and is tracked separately so it doesn't block
/// closing out the recording-specific parts of this punch list.
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

        // File > New Local Shell (ground-truthed earlier: Down#2 in the File menu).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 2);
        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var shellTab = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText("Local Shell")),
            TimeSpan.FromSeconds(10));
        shellTab.Should().NotBeNull("New Local Shell should have opened a real tab");

        // Session > Start Recording… (first item, auto-highlighted - 0 Downs).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 0);

        var consentDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
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
        var saveDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(15));
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
        var saveDialogClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
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
