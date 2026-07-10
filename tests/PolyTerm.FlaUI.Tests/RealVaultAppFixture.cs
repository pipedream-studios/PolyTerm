using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Launches the real, already-built <c>PolyTerm.exe</c> against the user's real, non-isolated vault -
/// deliberately the opposite of <see cref="PolyTermAppFixture"/>'s <c>POLYTERM_DATA_DIR</c> isolation.
/// Only for tests that need a specific, already-saved profile the isolated vault can't have (e.g. the
/// "windows" RDP profile with real credentials the user added specifically for
/// <see cref="RdpCertificateTrustTests"/>) - this never creates or modifies anything in the real vault
/// itself, only connects to a profile that already exists there.
/// </summary>
public sealed class RealVaultAppFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window MainWindow { get; }

    public RealVaultAppFixture()
    {
        Win32ForceForeground.MakeCurrentProcessDpiAware();

        var exePath = FindExePath();
        App = Application.Launch(exePath);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("PolyTerm's main window did not appear within 15 seconds.");

        Thread.Sleep(2000);
    }

    private static string FindExePath()
    {
        var testOutputDir = AppContext.BaseDirectory;
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
    }
}
