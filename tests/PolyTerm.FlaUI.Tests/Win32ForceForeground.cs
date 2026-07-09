using System.Runtime.InteropServices;

namespace PolyTerm.FlaUI.Tests;

/// <summary>
/// Windows enforces a "foreground lock" that normally blocks a background process from stealing
/// focus from whatever window the user is actually using — confirmed directly in this environment:
/// launching PolyTerm and calling FlaUI's own <c>Window.SetForeground()</c>/<c>Focus()</c> did not
/// bring it to the front while a different real application had focus, and a physical click aimed at
/// its expected screen location landed on that other (real, unrelated) foreground window instead.
/// This uses the standard <c>AttachThreadInput</c> workaround — temporarily sharing input state with
/// the foreground thread, which Windows does allow to call <c>SetForegroundWindow</c> — so interaction
/// can be aimed reliably regardless of what the user currently has focused.
/// </summary>
internal static class Win32ForceForeground
{
    public static nint CurrentForegroundWindow => GetForegroundWindow();

    // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>
    /// Forces the *current* process into per-monitor-v2 DPI awareness at runtime, matching
    /// PolyTerm.App's own app.manifest declaration. Needed because the actual running process during
    /// `dotnet test` is the test host (<c>testhost.exe</c>), not this project's own binary — an
    /// <c>&lt;ApplicationManifest&gt;</c> embedded in a test *library* has no effect on that host
    /// process's DPI awareness. Confirmed directly: without this, physical clicks landed well off
    /// their intended target (in the menu bar instead of the ribbon) — the un-aware test host's screen
    /// coordinates were being silently rescaled by Windows, while UIA (used to compute the intended
    /// click point) reports real physical-pixel coordinates.
    /// </summary>
    public static void MakeCurrentProcessDpiAware() => SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    public static void Apply(nint hwnd)
    {
        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = GetWindowThreadProcessId(hwnd, out _);
        var currentThreadId = GetCurrentThreadId();

        ShowWindow(hwnd, SW_RESTORE);

        if (foregroundThreadId != targetThreadId)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
            AttachThreadInput(currentThreadId, targetThreadId, true);
        }

        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);

        if (foregroundThreadId != targetThreadId)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
            AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
