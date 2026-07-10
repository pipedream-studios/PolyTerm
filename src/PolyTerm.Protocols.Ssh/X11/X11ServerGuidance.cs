namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>
/// Platform-appropriate install guidance shown when X11 forwarding is enabled but
/// <see cref="X11DisplayEndpoint.IsReachable"/> says nothing is listening at the configured display.
/// Takes platform flags as parameters (matching <see cref="X11DisplayEndpoint.Resolve"/>'s own
/// pattern) rather than reading <c>RuntimeInformation</c> itself, so this stays pure and testable on
/// any OS.
/// </summary>
internal static class X11ServerGuidance
{
    public static string BuildUnreachableMessage(bool isWindows, bool isMacOs) => isWindows
        ? "No X server detected at the configured display — install and run VcXsrv (https://sourceforge.net/projects/vcxsrv/), then reconnect."
        : isMacOs
            ? "No X server detected at the configured display — install and run XQuartz (https://www.xquartz.org/), then reconnect."
            : "No X server detected at the configured display — start a local X server before reconnecting.";
}
