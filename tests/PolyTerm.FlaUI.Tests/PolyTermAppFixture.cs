using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
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

        var exePath = FindExePath();
        var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = false };
        startInfo.Environment["POLYTERM_DATA_DIR"] = _isolatedDataDir;
        App = Application.Launch(startInfo);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("PolyTerm's main window did not appear within 15 seconds.");

        // The main window can appear before its DataContext/DI-resolved ViewModel finishes wiring up
        // (vault load, etc.) — a short grace period avoids racing that startup window.
        Thread.Sleep(2000);
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
    /// a hardcoded absolute path — walks up from <c>tests/PolyTerm.FlaUI.Tests/bin/&lt;config&gt;/&lt;tfm&gt;</c>
    /// to the repo root, then down into <c>src/PolyTerm.App/bin/&lt;config&gt;/net8.0-windows</c>. Assumes
    /// <c>PolyTerm.App</c> has already been built in the same configuration as this test project.
    /// </summary>
    private static string FindExePath()
    {
        var testOutputDir = AppContext.BaseDirectory; // .../tests/PolyTerm.FlaUI.Tests/bin/<Config>/<Tfm>/
        var configName = new DirectoryInfo(testOutputDir).Parent?.Name
            ?? throw new InvalidOperationException($"Could not determine build configuration from '{testOutputDir}'.");
        var repoRoot = Path.GetFullPath(Path.Combine(testOutputDir, "..", "..", "..", "..", ".."));
        var exePath = Path.Combine(repoRoot, "src", "PolyTerm.App", "bin", configName, "net8.0-windows", "PolyTerm.exe");

        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                $"Could not find PolyTerm.exe at '{exePath}'. Build src/PolyTerm.App in the '{configName}' " +
                "configuration before running these tests.", exePath);

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
        Automation.Dispose();
        App.Dispose();

        try { Directory.Delete(_isolatedDataDir, recursive: true); }
        catch { /* best-effort cleanup of the isolated test data dir - not worth failing the test over */ }
    }
}
