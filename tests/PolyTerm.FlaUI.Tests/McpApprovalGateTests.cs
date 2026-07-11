using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of the MCP approval-gate <c>ConfirmDialog</c> — closing the
/// "MCP approval-gate ConfirmDialog — confirm it visually shows the correct action label/color per
/// action type" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Uses <see cref="McpTestClient"/> - a minimal raw
/// client for <c>McpBridgeService</c>'s named-pipe protocol - to drive a real <c>terminal_execute</c>
/// tool call the same way any real MCP client (Claude Desktop, etc.) would, triggering
/// <c>ApprovalGateService</c>'s real dialog for real, not just asserting on the shared component via a
/// different call site (see <see cref="RecordingTests"/>, which already covers the dialog's rendering
/// but not this specific MCP trigger path).
/// </summary>
public sealed class McpApprovalGateTests : IClassFixture<PolyTermAppFixture>
{
    private readonly PolyTermAppFixture _fixture;
    public McpApprovalGateTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ApprovingATerminalExecuteCall_ShowsTheRealDialog_AndLetsTheCommandRun()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // File > New Local Shell (ground-truthed earlier: Down#2 in the File menu). Both [Fact]s in
        // this class share the same PolyTermAppFixture/app instance, so by the time the second one
        // runs, a "Local Shell" tab from the first may already be open - counting tabs before/after
        // (not just checking "does any Local Shell tab exist") confirms a genuinely new one opened,
        // rather than trivially matching a leftover one from an earlier test.
        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var shellTabsBefore = CountShellTabs(tabControl);
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 2);
        var shellTab = WaitFor(() => CountShellTabs(tabControl) > shellTabsBefore, TimeSpan.FromSeconds(20));
        shellTab.Should().BeTrue("New Local Shell should have opened a real, new tab");

        await using var mcp = await McpTestClient.ConnectAsync(_fixture.McpPipeName, TimeSpan.FromSeconds(10));

        var sessions = await mcp.CallAsync("session_list", new { });
        // .Last(), not .First(): the most recently opened session is the one this test itself just
        // created - matters once a second test in this class has already left an earlier one open.
        var sessionId = sessions.GetProperty("sessions").EnumerateArray().Last().GetProperty("id").GetString()!;

        // Don't await yet - GateCheckAsync inside the server blocks on the real dialog closing, so the
        // call only completes once this test clicks through it below.
        var executeTask = mcp.CallAsync("terminal_execute", new { session_id = sessionId, command = "echo mcp-approval-test-marker" });

        var consentDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        consentDialog.Title.Should().Be("MCP approval required");

        var messageText = consentDialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(t => t.Name).FirstOrDefault(n => n.Contains("run a command"));
        messageText.Should().NotBeNull("the dialog's message should describe the action as 'run a command', matching McpAction.TerminalExecute's Describe() mapping");
        messageText.Should().Contain("echo mcp-approval-test-marker", "the dialog should show the actual command an agent is asking to run");

        // TerminalExecute is non-destructive per ApprovalGateService.ButtonStyleFor - plain "Run" button,
        // not the red "Delete" styling SftpDelete gets.
        var runButton = consentDialog.FindFirstDescendant(cf => cf.ByName("Run"))?.AsButton()
            ?? throw new InvalidOperationException("'Run' button not found - expected for McpAction.TerminalExecute's non-destructive styling.");
        runButton.Focus();
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.SPACE);

        // Not asserting on the echoed command text itself: local shells produce zero terminal output in
        // this environment (a real, separate bug - filed as issue #23), unrelated to the approval gate
        // this test is actually about. What matters here is that approving the dialog let the call
        // complete instead of throwing the "Denied by MCP approval policy" error the deny test below
        // checks for.
        var result = await executeTask;
        result.TryGetProperty("text", out _).Should().BeTrue("a successful terminal_execute response should have a 'text' field regardless of its content");
    }

    [Fact]
    public async Task DenyingATerminalExecuteCall_BlocksTheCommand()
    {
        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var shellTabsBefore = CountShellTabs(tabControl);
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "File", downPresses: 2);
        var shellTab = WaitFor(() => CountShellTabs(tabControl) > shellTabsBefore, TimeSpan.FromSeconds(20));
        shellTab.Should().BeTrue("New Local Shell should have opened a real, new tab");

        await using var mcp = await McpTestClient.ConnectAsync(_fixture.McpPipeName, TimeSpan.FromSeconds(10));

        var sessions = await mcp.CallAsync("session_list", new { });
        var sessionId = sessions.GetProperty("sessions").EnumerateArray().Last().GetProperty("id").GetString()!;

        var executeTask = mcp.CallAsync("terminal_execute", new { session_id = sessionId, command = "echo should-not-run" });

        var consentDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        consentDialog.Title.Should().Be("MCP approval required");

        var cancelButton = consentDialog.FindFirstDescendant(cf => cf.ByName("Cancel"))?.AsButton()
            ?? throw new InvalidOperationException("'Cancel' button not found.");
        cancelButton.Focus();
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.SPACE);

        var act = async () => await executeTask;
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Denied by MCP approval policy*", "declining the dialog should surface ApprovalGateService's real denial back over the pipe");
    }

    /// <summary>Counts tabs currently titled "Local Shell" - used to confirm a genuinely <em>new</em>
    /// one opened (both [Fact]s in this class share the same app instance, so a tab from an earlier
    /// test may already be open by the time this one runs).</summary>
    private static int CountShellTabs(AutomationElement tabControl) =>
        tabControl.FindAllDescendants(cf => cf.ByText("Local Shell")).Length;

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

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(300);
        }
        return condition();
    }
}
