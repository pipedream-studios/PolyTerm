using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Real, end-to-end automated coverage of SSH auto-reconnect against a real server — closing the
/// "SSH auto-reconnect — does the tab visibly recover" line of the manual click-test punch list
/// (https://github.com/chris3081/PolyTerm/issues/1). Entirely keyboard-driven (see
/// <see cref="KeyboardUiActions"/>).
/// </summary>
/// <remarks>
/// Needs a real, reachable Ubuntu test server and a dedicated, disposable SSH keypair added to its
/// <c>~/.ssh/authorized_keys</c> - not a resource CI or every dev machine has, so this skips gracefully
/// (not a failure) when the key file or the host/username/vault-profile-id environment variables below
/// aren't configured, matching this project's existing precedent for other real-infrastructure-dependent
/// tests (e.g. the X11 forwarding tests skipping without a local X server). None of this test's real
/// server details are committed to the repo - they're only ever set in this developer's own local
/// environment. The key defaults to this session's scratchpad path; override via
/// <c>POLYTERM_TEST_SSH_KEY</c> if it's ever relocated somewhere more durable.
/// </remarks>
public sealed class SshAutoReconnectTests : IClassFixture<PolyTermAppFixture>
{
    private static readonly string? TestHost = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_HOST");
    private static readonly string? TestUsername = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_USERNAME");

    // The *real*, non-isolated vault's profile ID for the same test server, used only to run
    // `sudo pkill ...` on it via a separate process - not the profile this test itself connects with.
    private static readonly string? RealVaultProfileId = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID");

    private static readonly string DefaultKeyPath = Path.Combine(
        Path.GetTempPath(), "claude", "D--code-PolyTerm", "72096780-787f-4b99-be25-e4392a21d0d4",
        "scratchpad", "flaui-test-keys", "ssh_test_server_key");

    private readonly PolyTermAppFixture _fixture;
    public SshAutoReconnectTests(PolyTermAppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BreakingConnectivityServerSide_ShowsReconnectingThenRecovers()
    {
        var keyPath = Environment.GetEnvironmentVariable("POLYTERM_TEST_SSH_KEY") ?? DefaultKeyPath;
        if (!File.Exists(keyPath) || string.IsNullOrEmpty(TestHost) || string.IsNullOrEmpty(TestUsername) || string.IsNullOrEmpty(RealVaultProfileId))
        {
            Console.WriteLine($"SKIP: test SSH key not found at '{keyPath}', or POLYTERM_TEST_SSH_HOST/POLYTERM_TEST_SSH_USERNAME/POLYTERM_TEST_SSH_REAL_VAULT_PROFILE_ID not set - this test needs a real SSH test server and a dedicated key added to its authorized_keys. See this class's own doc comment.");
            return;
        }

        var handle = _fixture.MainWindow.Properties.NativeWindowHandle.Value;
        Win32ForceForeground.Apply(handle);
        Thread.Sleep(300);

        const string profileName = "ssh-test-server-flaui-test";
        var newConnectionWindow = KeyboardUiActions.ActivateTopMenuItemAndWaitForWindow(_fixture.MainWindow, "File", downPresses: 1);

        var nameBox = newConnectionWindow.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionNameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionNameInput not found.");
        nameBox.Focus();
        Keyboard.Type(profileName);
        Thread.Sleep(150);

        var hostBox = newConnectionWindow.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionHostInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionHostInput not found.");
        hostBox.Focus();
        Keyboard.Type(TestHost!);
        Thread.Sleep(150);

        var usernameBox = newConnectionWindow.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionUsernameInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionUsernameInput not found.");
        usernameBox.Focus();
        Keyboard.Type(TestUsername!);
        Thread.Sleep(150);

        var privateKeyBox = newConnectionWindow.FindFirstDescendant(cf => cf.ByAutomationId("ConnectionPrivateKeyPathInput"))?.AsTextBox()
            ?? throw new InvalidOperationException("ConnectionPrivateKeyPathInput not found.");
        privateKeyBox.Focus();
        Keyboard.Type(keyPath);
        Thread.Sleep(150);

        KeyboardUiActions.ActivateButtonByName(newConnectionWindow, "Save");
        var newConnectionClosed = SpinWait.SpinUntil(() => KeyboardUiActions.NoOwnedWindowRemains(_fixture.MainWindow), TimeSpan.FromSeconds(10));
        newConnectionClosed.Should().BeTrue("Save should close the New Connection dialog");

        // "Go to connection" via the Command Palette - reuses the tree row's own ConnectCommand, the
        // only reliable keyboard path to connecting a saved profile (the tree's own double-tap gesture
        // is a mouse-only affordance with no keyboard equivalent).
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

        var connectedTab = WaitForTabTitle(profileName, TimeSpan.FromSeconds(20));
        connectedTab.Should().BeTrue($"the tab should reach the plain '{profileName}' title once connected");

        // Break connectivity server-side via a completely separate process, using the real (not
        // isolated) vault's already-working profile for the same test server.
        KillLingeringSshSessionOnTestServer();

        var reconnecting = WaitForTabTitle($"{profileName} (reconnecting…)", TimeSpan.FromSeconds(20));
        reconnecting.Should().BeTrue("the tab should visibly show '(reconnecting…)' after the server-side connection drop");

        var recovered = WaitForTabTitle(profileName, TimeSpan.FromSeconds(40));
        recovered.Should().BeTrue("the tab should recover back to the plain title once sshd is back up and auto-reconnect succeeds");
    }

    private bool WaitForTabTitle(string exactTitle, TimeSpan timeout)
    {
        var tabControl = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SessionTabControl"))
            ?? throw new InvalidOperationException("SessionTabControl not found.");
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (KeyboardUiActions.TryFindFirst(tabControl, cf => cf.ByText(exactTitle)) is not null)
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// Kills the lingering interactive sshd session on the real test server via the real PolyTerm.Cli
    /// and the real (non-isolated) vault's already-approved profile - not the disposable test
    /// key/profile this test itself just created. <c>systemctl restart ssh</c> was tried first and
    /// confirmed *not* to work for this: restarting the daemon only affects the listening socket for new
    /// connections, not already-established sessions, each of which keeps running as its own forked
    /// child process independent of the parent daemon. Killing the actual per-session process is what's
    /// needed.
    /// </summary>
    private static void KillLingeringSshSessionOnTestServer()
    {
        var testDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var cliProject = Path.Combine(repoRoot, "src", "PolyTerm.Cli");

        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                "run", "--project", cliProject, "-c", "Release", "--",
                "exec", RealVaultProfileId!, $"sudo pkill -u {TestUsername} -f 'sshd: {TestUsername}@'",
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start polyterm CLI to restart sshd.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
        Console.WriteLine($"Kill-session command exited={exited}, exitCode={(exited ? proc.ExitCode : -1)}");
        Console.WriteLine($"stdout:\n{stdout.Result}");
        if (stderr.Result.Length > 0) Console.WriteLine($"stderr:\n{stderr.Result}");
    }
}
