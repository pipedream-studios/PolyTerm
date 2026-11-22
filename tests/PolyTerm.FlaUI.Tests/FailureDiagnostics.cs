using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Exceptions;
using Xunit;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Saves what was actually on screen when a FlaUI test failed (issue #206): a full-screen screenshot
/// and a dump of the main window's UIA tree, which includes its owned dialogs (they're UIA
/// descendants of it, see <see cref="KeyboardUiActions"/>). Before this, a CI failure left nothing
/// to go on beyond the assertion message.
/// <para>
/// Files go to <c>POLYTERM_FLAUI_ARTIFACTS_DIR</c> when it's set (CI points it inside
/// <c>TestResults</c> so the upload step collects them), otherwise to <c>FlaUIFailures</c> next to
/// the test assembly. Each file is named after the test. Capturing is best-effort: a failure here is
/// logged, never thrown, so it can't hide the test's own failure.
/// </para>
/// </summary>
internal static class FailureDiagnostics
{
    private const int MaxTreeNodes = 5000;

    public static string ArtifactsDirectory =>
        Environment.GetEnvironmentVariable("POLYTERM_FLAUI_ARTIFACTS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "FlaUIFailures");

    /// <summary>Captures diagnostics if the test that just ran failed. Call from the test class's
    /// <c>Dispose</c>: xUnit v3 only fills in <see cref="TestContext.TestState"/> once the test has
    /// finished.</summary>
    public static void CaptureIfFailed(PolyTermAppFixture fixture)
    {
        var context = TestContext.Current;
        if (context.TestState?.Result != TestResult.Failed) return;

        var testName = context.Test?.TestDisplayName ?? "unknown-test";
        var baseName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Sanitize(testName)}";

        try
        {
            Directory.CreateDirectory(ArtifactsDirectory);
        }
        catch (Exception ex)
        {
            Log($"FailureDiagnostics: couldn't create '{ArtifactsDirectory}': {ex.Message}");
            return;
        }

        TryWrite(baseName + ".png", path => Capture.Screen().ToFile(path));
        TryWrite(baseName + ".uia.txt", path => File.WriteAllText(path, DumpTree(fixture)));
    }

    private static void TryWrite(string fileName, Action<string> write)
    {
        var path = Path.Combine(ArtifactsDirectory, fileName);
        try
        {
            write(path);
            Log($"FailureDiagnostics: saved {path}");
        }
        catch (Exception ex)
        {
            Log($"FailureDiagnostics: couldn't save {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The main window's UIA subtree, one indented line per element, plus which window
    /// had the OS foreground. Capped at <see cref="MaxTreeNodes"/> elements.</summary>
    private static string DumpTree(PolyTermAppFixture fixture)
    {
        var sb = new StringBuilder();
        var foreground = Win32ForceForeground.CurrentForegroundWindow;
        sb.AppendLine(CultureInfo.InvariantCulture, $"Foreground HWND: 0x{foreground:X}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PolyTerm.exe exited: {fixture.App.HasExited}");
        sb.AppendLine();

        var walker = fixture.Automation.TreeWalkerFactory.GetControlViewWalker();
        var count = 0;

        void Visit(AutomationElement element, int depth)
        {
            if (count++ >= MaxTreeNodes)
            {
                if (count == MaxTreeNodes + 1) sb.AppendLine(CultureInfo.InvariantCulture, $"... stopped after {MaxTreeNodes} elements");
                return;
            }

            sb.Append(' ', depth * 2).AppendLine(Describe(element, foreground));

            AutomationElement? child;
            try { child = walker.GetFirstChild(element); }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException) { return; }

            while (child is not null)
            {
                Visit(child, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch (Exception ex) when (ex is COMException or ElementNotAvailableException) { break; }
            }
        }

        Visit(fixture.MainWindow, 0);
        return sb.ToString();
    }

    private static string Describe(AutomationElement element, nint foreground)
    {
        try
        {
            var p = element.Properties;
            var hwnd = p.NativeWindowHandle.ValueOrDefault;
            var parts = new List<string> { p.ControlType.ValueOrDefault.ToString() };
            if (p.Name.ValueOrDefault is { Length: > 0 } name) parts.Add($"\"{name}\"");
            if (p.AutomationId.ValueOrDefault is { Length: > 0 } id) parts.Add($"id={id}");
            if (p.ClassName.ValueOrDefault is { Length: > 0 } cls) parts.Add($"class={cls}");
            if (hwnd != 0) parts.Add($"hwnd=0x{hwnd:X}{(hwnd == foreground ? " (foreground)" : "")}");
            if (!p.IsEnabled.ValueOrDefault) parts.Add("disabled");
            if (p.IsOffscreen.ValueOrDefault) parts.Add("offscreen");
            if (p.HasKeyboardFocus.ValueOrDefault) parts.Add("FOCUSED");
            parts.Add($"rect={p.BoundingRectangle.ValueOrDefault}");
            return string.Join(" ", parts);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return $"<unavailable: {ex.GetType().Name}>";
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static void Log(string message) => Console.WriteLine(message);
}
