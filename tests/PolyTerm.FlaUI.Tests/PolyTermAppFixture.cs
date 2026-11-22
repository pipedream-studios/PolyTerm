using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Launches the real, already-built <c>PolyTerm.exe</c> and finds its main window via a real
/// <see cref="UIA3Automation"/> — one instance per test class (via <c>IClassFixture</c>), since
/// launching the app is expensive relative to the rest of this repo's test suite. This is real native
/// window automation against the actual shipped app, not a mock or a headless stand-in — confirmed
/// viable in an earlier throwaway spike (see docs/testing.md's FlaUI section) before being made
/// permanent here.
/// </summary>
public sealed class PolyTermAppFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window MainWindow { get; }

    private readonly string _isolatedDataDir;
    private readonly KillOnCloseJob _job = new();

    /// <summary>The private MCP pipe name this launched instance listens on (see
    /// <c>POLYTERM_MCP_PIPE_NAME</c> in the constructor) - a real MCP client test can connect to this
    /// exact pipe without any risk of colliding with (or accidentally talking to) an already-running
    /// real PolyTerm.exe sharing the default "polyterm" pipe name.</summary>
    public string McpPipeName { get; }

    public PolyTermAppFixture()
    {
        // Must happen before any screen-coordinate Win32 calls (see Win32ForceForeground's own doc
        // comment for why an un-aware test host process needs this at all).
        Win32ForceForeground.MakeCurrentProcessDpiAware();

        // PolyTerm.exe's app-data directory (settings, macros, and — critically — the real
        // connection-profile vault) defaults to the real per-user %AppData%/PolyTerm used by this
        // developer's actual, hand-maintained PolyTerm installation (dozens of real SSH/RDP profiles).
        // Any test that actually saves a profile (not just Cancels out) would otherwise write real
        // test data straight into that vault. POLYTERM_DATA_DIR (see AppPaths.ConfigDirectory) redirects
        // the whole launched process at a fresh, disposable temp directory instead — deleted in Dispose.
        _isolatedDataDir = Path.Combine(Path.GetTempPath(), "PolyTermFlaUITests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedDataDir);

        McpPipeName = $"polyterm-flaui-test-{Guid.NewGuid():N}";

        var exePath = FindExePath();
        var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = false };
        startInfo.Environment["POLYTERM_DATA_DIR"] = _isolatedDataDir;
        startInfo.Environment["POLYTERM_MCP_PIPE_NAME"] = McpPipeName;
        App = Application.Launch(startInfo);
        // Before anything else: WebView2 and browser processes PolyTerm starts later join the job too,
        // so Dispose can kill the whole tree - see KillOnCloseJob for the release-run hang this fixes.
        _job.Assign(App.ProcessId);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("PolyTerm's main window did not appear within 15 seconds.");

        // The main window can appear before its DataContext/DI-resolved ViewModel finishes wiring up
        // (vault load, etc.) — a short grace period avoids racing that startup window. 3s rather than
        // the original 2s: confirmed directly that running this suite's now-larger full set of test
        // classes back-to-back (13 tests, several real app launches) is measurably more prone to an
        // early keystroke landing before the window has *fully* settled than any single class run in
        // isolation ever was - consistent with, not worse than, this project's existing documented
        // foreground/timing sensitivity (see Win32ForceForeground's and KeyboardDiagnosticTests' own
        // comments), just needing more headroom now that there's more of it running in sequence.
        Thread.Sleep(3000);
    }

    /// <summary>
    /// Puts the shared app back to "just the main window, in the foreground" before a test (issue
    /// #204). Every test class constructor calls this; xUnit runs the constructor before each test.
    /// <para>
    /// Each test class shares one launched app, and nothing else resets it between tests, so a test
    /// that failed with a dialog still open used to fail every following test in its class too. On PR
    /// #201's first CI run the VNC test left its New Connection dialog open, and the next test (RDP)
    /// then failed on it: one real failure reported as two.
    /// </para>
    /// <para>
    /// Closes leftovers innermost-first (a modal child has to close before the owner it blocks will
    /// respond): <c>WM_CLOSE</c>, the same as the title-bar close button, then UIA's WindowPattern,
    /// then Escape. Logs each one, because a leftover means an earlier test failed or didn't clean up.
    /// Throws if one won't close, so the test fails here with a clear reason instead of later on
    /// something unrelated.
    /// </para>
    /// </summary>
    public void ResetToMainWindow()
    {
        var leftovers = FindOwnedWindows();
        if (leftovers.Count > 0)
        {
            Console.WriteLine($"ResetToMainWindow: closing {leftovers.Count} window(s) left open by an earlier test: " +
                string.Join(", ", leftovers.Select(l => $"'{l.Title}'")));

            for (var i = leftovers.Count - 1; i >= 0; i--)
                CloseLeftover(leftovers[i]);

            var stillOpen = FindOwnedWindows();
            if (stillOpen.Count > 0)
                throw new InvalidOperationException(
                    "ResetToMainWindow couldn't close window(s) left open by an earlier test: " +
                    string.Join(", ", stillOpen.Select(l => $"'{l.Title}'")));
        }

        Win32ForceForeground.Apply(MainWindow.Properties.NativeWindowHandle.Value);
        Thread.Sleep(300);
    }

    private sealed record OwnedWindow(Window Window, nint Hwnd, string Title);

    /// <summary>Every window currently open under the main window, outermost first (UIA's descendant
    /// order). Skips child HWNDs hosted inside the main window (WebView2, RDP), which are part of a
    /// session tab rather than a dialog.</summary>
    private List<OwnedWindow> FindOwnedWindows()
    {
        var result = new List<OwnedWindow>();
        foreach (var element in KeyboardUiActions.TryFindAll(MainWindow, cf => cf.ByControlType(ControlType.Window)))
        {
            try
            {
                var hwnd = element.Properties.NativeWindowHandle.ValueOrDefault;
                if (hwnd != 0 && (!Win32ForceForeground.IsTopLevel(hwnd) || !Win32ForceForeground.IsOpen(hwnd))) continue;
                result.Add(new OwnedWindow(element.AsWindow(), hwnd, element.Name ?? ""));
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
            {
                // Closed while being listed - nothing to clean up.
            }
        }
        return result;
    }

    private static void CloseLeftover(OwnedWindow leftover)
    {
        var timeout = TimeSpan.FromSeconds(3);
        bool Closed() => KeyboardUiActions.IsDialogClosed(leftover.Window, leftover.Hwnd);

        if (leftover.Hwnd != 0)
        {
            Win32ForceForeground.RequestClose(leftover.Hwnd);
            if (SpinWait.SpinUntil(Closed, timeout)) return;
        }

        try
        {
            leftover.Window.Patterns.Window.PatternOrDefault?.Close();
            if (SpinWait.SpinUntil(Closed, timeout)) return;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            if (Closed()) return;
        }

        Console.WriteLine($"ResetToMainWindow: '{leftover.Title}' ignored WM_CLOSE and WindowPattern.Close, trying Escape.");
        if (leftover.Hwnd != 0) Win32ForceForeground.Apply(leftover.Hwnd);
        Thread.Sleep(200);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        SpinWait.SpinUntil(Closed, timeout);
    }

    /// <summary>
    /// Opens a fresh <see cref="IProfileStore"/> against the exact same isolated, per-test database
    /// the launched <c>PolyTerm.exe</c> is using (see <c>POLYTERM_DATA_DIR</c> in the
    /// constructor) — lets a test verify what was *actually persisted* by a real Save click, through
    /// the app's own real storage layer, rather than only inspecting in-memory UI state. Safe to call
    /// concurrently with the running app: <c>ProfileStore</c> opens and closes its own SQLite
    /// connection per call rather than holding one open.
    /// </summary>
    public IProfileStore OpenProfileStoreForVerification()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPolyTermEngine(new EngineOptions
        {
            DatabasePath = Path.Combine(_isolatedDataDir, "PolyTerm.db"),
        });
        return services.BuildServiceProvider().GetRequiredService<IProfileStore>();
    }

    /// <summary>
    /// Resolves the real build output path relative to this test assembly's own location, rather than
    /// a hardcoded absolute path — walks up from <c>tests/PolyTerm.FlaUI.Tests/bin/&lt;config&gt;</c> to
    /// the repo root, then searches <c>src/PolyTerm.App/bin/&lt;config&gt;/*/PolyTerm.exe</c> for
    /// whichever target framework subdirectory it actually landed in.
    ///
    /// This used to assume <c>PolyTerm.App</c> built to the *same* TFM folder name as this test
    /// assembly's own output directory (<c>PolyTerm.FlaUI.Tests.csproj</c> pins
    /// <c>net10.0-windows</c>, needed for FlaUI/UIA's Windows-only automation APIs regardless of what
    /// the App itself targets) — a hardcoded/derived TFM assumption here has now gone stale twice:
    /// first across the net8.0-windows -> net10.0-windows migration, then again when
    /// <c>PolyTerm.App</c> stopped overriding <c>TargetFramework</c> locally and started inheriting
    /// the cross-platform-buildable default (plain <c>net10.0</c>) from <c>Directory.Build.props</c>,
    /// diverging from this project's own pinned TFM for the first time — confirmed the hard way via a
    /// real CI failure. Searching for the actual output instead of assuming a name sidesteps this
    /// whole class of staleness rather than fixing it a third time. Assumes <c>PolyTerm.App</c> has
    /// already been built in the same configuration as this test project.
    /// </summary>
    private static string FindExePath()
    {
        var testOutputDir = AppContext.BaseDirectory; // .../tests/PolyTerm.FlaUI.Tests/bin/<Config>/<Tfm>/
        var testOutputInfo = new DirectoryInfo(testOutputDir);
        var configName = testOutputInfo.Parent?.Name
            ?? throw new InvalidOperationException($"Could not determine build configuration from '{testOutputDir}'.");
        var repoRoot = Path.GetFullPath(Path.Combine(testOutputDir, "..", "..", "..", "..", ".."));
        var appBinDir = Path.Combine(repoRoot, "src", "PolyTerm.App", "bin", configName);

        if (!Directory.Exists(appBinDir))
            throw new DirectoryNotFoundException(
                $"'{appBinDir}' doesn't exist. Build src/PolyTerm.App in the '{configName}' configuration before running these tests.");

        string? exePath = null;
        foreach (var tfmDir in Directory.EnumerateDirectories(appBinDir))
        {
            var candidate = Path.Combine(tfmDir, "PolyTerm.exe");
            if (File.Exists(candidate))
            {
                exePath = candidate;
                break;
            }
        }

        if (exePath is null)
            throw new FileNotFoundException(
                $"Could not find PolyTerm.exe under '{appBinDir}' (checked every target framework subdirectory). " +
                $"Build src/PolyTerm.App in the '{configName}' configuration before running these tests.");

        return exePath;
    }

    /// <summary>
    /// Waits for the real process to actually exit (not just requests a graceful close) before
    /// returning. Each test class gets its own fixture, launching and tearing down a fresh
    /// <c>PolyTerm.exe</c> in sequence (parallelization is disabled — see <c>AssemblyInfo.cs</c>) — if
    /// this returned as soon as <c>Close()</c> was requested, the next class's fixture could launch and
    /// force its own window to the foreground while the previous process (and its window-close
    /// animation) was still settling, racing <see cref="Win32ForceForeground"/>'s foreground-lock
    /// workaround for the next test. Falls back to <c>Kill()</c> if graceful close doesn't finish within
    /// <c>CloseTimeout</c>, so a hung process can never linger into the next test class.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!App.HasExited)
                App.Close();
            if (!App.HasExited)
                App.Kill();
        }
        catch { /* best-effort — the process may already be gone */ }
        // Kills PolyTerm.exe if it's somehow still running, plus everything it started (WebView2, a browser).
        _job.Dispose();
        Automation.Dispose();
        App.Dispose();

        try { Directory.Delete(_isolatedDataDir, recursive: true); }
        catch { /* best-effort cleanup of the isolated test data dir - not worth failing the test over */ }
    }
}
