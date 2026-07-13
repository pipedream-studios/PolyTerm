using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using AwesomeAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, automated coverage of the SFTP panel against a real test server, real progress on the file
/// list's row-focus gap, and a re-documented, more precisely root-caused gap for the specific
/// "Permissions…" (chmod) context-menu line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1).
/// </summary>
/// <remarks>
/// Same real-infrastructure dependency as <see cref="SshAutoReconnectTests"/> - skips gracefully when
/// the disposable test SSH key or the host/username/vault-profile-id environment variables aren't
/// configured. See that class's own doc comment for details.
/// </remarks>
public sealed class SftpPermissionsTests : IClassFixture<PolyTermAppFixture>
{
    private static readonly string? TestHost = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_HOST");
    private static readonly string? TestUsername = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_USERNAME");
    private static readonly string? RealVaultProfileId = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID");
    private const string TargetFileName = "flaui-chmod-test.txt";

    private static readonly string DefaultKeyPath = Path.Combine(
        Path.GetTempPath(), "claude", "D--code-PolyTerm", "72096780-787f-4b99-be25-e4392a21d0d4",
        "scratchpad", "flaui-test-keys", "ssh_test_server_key");

    private static bool IsRealInfraConfigured =>
        string.IsNullOrEmpty(TestHost) is false && string.IsNullOrEmpty(TestUsername) is false && string.IsNullOrEmpty(RealVaultProfileId) is false;

    private readonly PolyTermAppFixture _fixture;
    public SftpPermissionsTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public void TogglingSftpPanel_ShowsARealFileListingFromTheServer()
    {
        var keyPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY") ?? DefaultKeyPath;
        if (!File.Exists(keyPath) || !IsRealInfraConfigured)
        {
            Console.WriteLine($"SKIP: test SSH key not found at '{keyPath}', or POLYTERM_TEST_SSH_HOST/POLYTERM_TEST_SSH_USERNAME/POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID not set - see SshAutoReconnectTests's own doc comment.");
            return;
        }

        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "ssh-test-server-sftp-test";
        CreateSshProfile(profileName, keyPath);
        ConnectViaCommandPalette(profileName);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var connected = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText(profileName)), TimeSpan.FromSeconds(20));
        connected.Should().NotBeNull($"the tab should reach the plain '{profileName}' title once connected");

        // Ground-truthed rather than assumed: the Session menu's navigable order skips disabled items
        // entirely (confirmed directly - "Stop Recording" is disabled here with no recording started,
        // and Down#1 landed straight on "Toggle SFTP Panel", not on the disabled item first).
        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 1);

        var fileList = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByControlType(ControlType.List)), TimeSpan.FromSeconds(10));
        fileList.Should().NotBeNull("the SFTP panel's file list should appear after toggling it on");

        // Real files on the real server (fixed by this test, not assumed): the private key was already
        // used to create ~/.ssh, and the target file for the (currently gap-blocked - see
        // SftpChmodViaContextMenu_IsAKnownGap below) Permissions test lives here too.
        var target = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByText(TargetFileName)), TimeSpan.FromSeconds(15));
        target.Should().NotBeNull($"'{TargetFileName}' should appear in the real file listing from the real server");
    }

    /// <summary>
    /// Real, verified progress on the SFTP row-focus half of the gap documented below: the original
    /// six-technique investigation concluded the file list's <see cref="ListBox"/> row never accepts real
    /// keyboard focus, but that conclusion rested on a targeting bug in the investigation itself, not a
    /// real Avalonia gap. Each row's accessible Name is its bound view-model's plain <c>ToString()</c>
    /// (already documented below), so locating a row "by its visible text" (<c>ByText</c>) actually finds
    /// the nested <see cref="ControlType.Text"/> label inside the row, not the row
    /// (<see cref="ControlType.ListItem"/>) itself - and <c>.Focus()</c> on that label is a no-op that was
    /// previously misread as "the ListBox refuses focus". Checking <c>IsKeyboardFocusable</c> directly
    /// (not just <c>HasKeyboardFocus</c>, which only tells you whether focus already landed) shows the
    /// <em>ListBox container</em> genuinely reports <c>False</c> (consistent with it not supporting UIA's
    /// SelectionPattern either), but each <em>row</em> reports <c>True</c> - and calling <c>.Focus()</c>
    /// directly on the row (found by walking up from the text label to its <c>ListItem</c> ancestor) does
    /// land real keyboard focus there, confirmed via <c>HasKeyboardFocus</c> immediately after.
    /// </summary>
    [Fact]
    public void SftpFileListRow_AcceptsRealKeyboardFocus_WhenTargetedDirectly()
    {
        var keyPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY") ?? DefaultKeyPath;
        if (!File.Exists(keyPath) || !IsRealInfraConfigured)
        {
            Console.WriteLine($"SKIP: test SSH key not found at '{keyPath}', or POLYTERM_TEST_SSH_HOST/POLYTERM_TEST_SSH_USERNAME/POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID not set - see SshAutoReconnectTests's own doc comment.");
            return;
        }

        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // Shares the app process (IClassFixture) with TogglingSftpPanel_... above, which leaves its own
        // tab open. A stale tab showing the same file can make a text-based search below find its
        // now-inactive copy instead of this test's own tab, so start from a clean slate.
        CloseAllOpenTabs();

        const string profileName = "ssh-test-server-chmod-test";
        CreateSshProfile(profileName, keyPath);
        ConnectViaCommandPalette(profileName);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var connected = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText(profileName)), TimeSpan.FromSeconds(20));
        connected.Should().NotBeNull($"the tab should reach the plain '{profileName}' title once connected");

        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 1);

        var fileList = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByControlType(ControlType.List)), TimeSpan.FromSeconds(10));
        fileList.Should().NotBeNull("the SFTP panel's file list should appear after toggling it on");
        fileList!.Properties.IsKeyboardFocusable.ValueOrDefault.Should().BeFalse(
            "the ListBox container itself is not a real UIA focus stop here (consistent with it not supporting SelectionPattern either)");

        var textEl = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByText(TargetFileName)), TimeSpan.FromSeconds(15));
        textEl.Should().NotBeNull($"'{TargetFileName}' should appear in the real file listing from the real server");

        AutomationElement? row = textEl;
        while (row is not null && row.ControlType != ControlType.ListItem)
            row = row.Parent;
        row.Should().NotBeNull("the text label's ListItem ancestor should exist");
        row!.Properties.IsKeyboardFocusable.ValueOrDefault.Should().BeTrue(
            "unlike the ListBox container, each row (ListItem) does advertise itself as a real UIA focus stop");

        // Several dialogs (New Connection, Command Palette) have opened and closed since the one
        // foreground grab at the top of this test - re-assert it right before the focus-sensitive step.
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(200);

        row.Focus();
        Thread.Sleep(300);
        row.Properties.HasKeyboardFocus.ValueOrDefault.Should().BeTrue(
            "real keyboard focus should land on the row (ListItem) itself once .Focus() targets it directly, " +
            "rather than the nested text label the original investigation mistakenly targeted");
    }

    /// <summary>
    /// The row <see cref="ContextMenu"/> itself is still unreachable from the keyboard (see this class's
    /// own git history / #25 for the full investigation: Avalonia 11.2.7 has no keyboard-driven path to
    /// <c>ContextRequestedEvent</c> at all, confirmed by decompiling the shipped assemblies), but #25
    /// closed that gap a different way - a toolbar "Permissions…" button
    /// (<c>SftpChmodSelectedButton</c>) next to Up/Refresh/Upload/Download that acts on
    /// <c>SelectedEntry</c> directly. This test drives that real button end to end: focuses the target
    /// row, selects it with a real <c>Space</c> keypress (Avalonia's default ListBox/ListBoxItem
    /// selection gesture), clicks "Permissions…", fills in the real <see cref="InputDialog"/>, and
    /// verifies via a second, independent SSH channel (<c>PolyTerm.Cli</c> against the real vault
    /// profile, not the disposable one this test creates) that the mode actually changed on the real
    /// server - not just that the dialog closed.
    /// </summary>
    [Fact]
    public void SftpChmodViaToolbarButton_ChangesRealPermissionsOnTheServer()
    {
        var keyPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY") ?? DefaultKeyPath;
        if (!File.Exists(keyPath) || !IsRealInfraConfigured)
        {
            Console.WriteLine($"SKIP: test SSH key not found at '{keyPath}', or POLYTERM_TEST_SSH_HOST/POLYTERM_TEST_SSH_USERNAME/POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID not set - see SshAutoReconnectTests's own doc comment.");
            return;
        }

        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        // Shares the app process with the other tests in this class (IClassFixture) - start clean.
        CloseAllOpenTabs();

        const string profileName = "ssh-test-server-chmod-toolbar-test";
        CreateSshProfile(profileName, keyPath);
        ConnectViaCommandPalette(profileName);

        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var connected = WaitFor(() => KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText(profileName)), TimeSpan.FromSeconds(20));
        connected.Should().NotBeNull($"the tab should reach the plain '{profileName}' title once connected");

        KeyboardUiActions.ActivateTopMenuItem(_fixture.MainWindow, "Session", downPresses: 1);

        var fileList = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByControlType(ControlType.List)), TimeSpan.FromSeconds(10));
        fileList.Should().NotBeNull("the SFTP panel's file list should appear after toggling it on");

        var textEl = WaitFor(() => KeyboardUiActions.TryFindFirst(_fixture.MainWindow, cf => cf.ByText(TargetFileName)), TimeSpan.FromSeconds(15));
        textEl.Should().NotBeNull($"'{TargetFileName}' should appear in the real file listing from the real server");

        AutomationElement? row = textEl;
        while (row is not null && row.ControlType != ControlType.ListItem)
            row = row.Parent;
        row.Should().NotBeNull("the text label's ListItem ancestor should exist");

        var chmodButton = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SftpChmodSelectedButton"))?.AsButton()
            ?? throw new InvalidOperationException("SftpChmodSelectedButton not found.");
        chmodButton.IsEnabled.Should().BeFalse("the toolbar Permissions button should start disabled with no row selected");

        Win32ForceForeground.Apply(handle);
        Thread.Sleep(200);

        row!.Focus();
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.SPACE);
        Thread.Sleep(300);

        chmodButton.IsEnabled.Should().BeTrue(
            "a real Space keypress on the focused row should select it (Avalonia's default ListBox " +
            "selection gesture), enabling the toolbar button that's bound to SelectedEntry");

        KeyboardUiActions.ActivateButton(chmodButton);

        var inputDialog = KeyboardUiActions.WaitForOwnedWindow(_fixture.MainWindow, TimeSpan.FromSeconds(10));
        inputDialog.Title.Should().Contain("Permissions", "the InputDialog opened by ChmodAsync is titled from the prompt text itself");

        var inputBox = inputDialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox()
            ?? throw new InvalidOperationException("InputBox not found in the Permissions dialog.");
        inputBox.Focus();
        Thread.Sleep(150);
        // InputDialog.OnOpened already SelectAll()s the initial value ("644"), so typing replaces it.
        Keyboard.Type("640");
        Thread.Sleep(150);

        KeyboardUiActions.ActivateButtonByName(inputDialog, "OK");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("OK should close the Permissions dialog");

        var mode = StatModeOnTestServer(TargetFileName);
        mode.Should().Be("640", "the chmod should have actually reached the real server, not just closed the dialog");

        // Restore the baseline mode ChmodAsync's own initial-value logic assumes for non-directories,
        // so repeat runs of this test (and TogglingSftpPanel_... above, sharing the same real file)
        // start from the same "644" state.
        RunCliExec($"chmod 644 {TargetFileName}");
    }

    /// <summary>Runs <c>stat -c '%a'</c> against <paramref name="fileName"/> (relative to the SSH login's
    /// home directory) on the real test server via the real, already-approved vault profile - a second,
    /// independent channel from the app under test, so this is a genuine external check, not just
    /// re-reading what the app itself believes happened. <see cref="RunCliExec"/>'s output is the whole
    /// captured shell transcript (login banner, echoed command, ANSI mode-set sequences, prompt) - the
    /// mode itself is the one line matching 3-4 octal digits on their own.</summary>
    private static string StatModeOnTestServer(string fileName)
    {
        var output = RunCliExec($"stat -c '%a' {fileName}");
        // RunCliExec's output is the whole raw shell transcript (login banner, echoed command, ANSI
        // escape sequences, prompt). Confirmed live via a hex dump: right after the bracketed-paste-off
        // sequence ("\x1b[?2004l") the shell emits a bare '\r' before the real command output, so the
        // mode ends up as "\r640\r\n" rather than a clean line start - strip escape sequences first, then
        // allow an optional leading '\r' when matching the 3-4 octal-digit mode.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(output, "\x1b\\[[0-9;?]*[a-zA-Z]", "");
        var match = System.Text.RegularExpressions.Regex.Match(cleaned, @"(?m)^\r?(?<mode>[0-7]{3,4})\r?$");
        return match.Success ? match.Groups["mode"].Value : output;
    }

    private static string RunCliExec(string remoteCommand)
    {
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var cliProject = Path.Combine(repoRoot, "src", "PolyTerm.Cli");

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "--project", cliProject, "-c", "Release", "--", "exec", RealVaultProfileId!, remoteCommand },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start polyterm CLI.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
        Console.WriteLine($"CLI exec '{remoteCommand}' exited={exited}, exitCode={(exited ? proc.ExitCode : -1)}");
        if (stderr.Result.Length > 0) Console.WriteLine($"stderr:\n{stderr.Result}");
        return stdout.Result;
    }

    private void CreateSshProfile(string profileName, string keyPath)
    {
        var dialog = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1, submenuDownPresses: 0);

        var nameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionNameInput not found.");
        nameBox.Focus();
        Keyboard.Type(profileName);
        Thread.Sleep(150);

        var hostBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionHostInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionHostInput not found.");
        hostBox.Focus();
        Keyboard.Type(TestHost!);
        Thread.Sleep(150);

        var usernameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionUsernameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionUsernameInput not found.");
        usernameBox.Focus();
        Keyboard.Type(TestUsername!);
        Thread.Sleep(150);

        var privateKeyBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionPrivateKeyPathInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionPrivateKeyPathInput not found.");
        privateKeyBox.Focus();
        Keyboard.Type(keyPath);
        Thread.Sleep(150);

        KeyboardUiActions.ActivateButtonByName(dialog, "Save");
        var closed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        closed.Should().BeTrue("Save should close the New Connection dialog");
    }

    private void ConnectViaCommandPalette(string profileName) => RunCommandPaletteCommand(profileName);

    /// <summary>Opens the Command Palette (Ctrl+Shift+P) and runs whatever real, keyboard-navigable
    /// entry matches <paramref name="searchText"/> most closely (a saved connection's profile name, or a
    /// built-in command like "Close Active Tab") - same mechanism, since the palette lists both.</summary>
    private void RunCommandPaletteCommand(string searchText)
    {
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
        Keyboard.Type(searchText);
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(500);
    }

    /// <summary>Closes every currently open session tab via the real "Close Active Tab" command-palette
    /// entry, so a test that shares its app process with earlier tests (via <c>IClassFixture</c>) starts
    /// from a clean, unambiguous slate rather than risking a stale tab's now-inactive content being found
    /// by a text-based search instead of the tab this test itself just created.</summary>
    private void CloseAllOpenTabs()
    {
        for (var i = 0; i < 8; i++)
        {
            var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"));
            if (tabControl is null) return;
            var anyTab = KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByControlType(ControlType.TabItem));
            if (anyTab is null) return;

            RunCommandPaletteCommand("Close Active Tab");
            Thread.Sleep(300);
        }
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
