using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PolyTerm.Protocols.Ssh.X11;

/// <summary>Where to reach the local X server that inbound X11-forwarded connections should be bridged to.</summary>
internal sealed record X11DisplayEndpoint(EndPoint Endpoint, int DisplayNumber, int ScreenNumber)
{
    /// <summary>
    /// Resolves the local X server from the <c>DISPLAY</c> environment variable if set (the standard
    /// convention every X client/server already follows), otherwise a platform default. Pure/testable —
    /// takes the environment variable value and OS as parameters rather than reading them itself.
    /// </summary>
    /// <remarks>
    /// <c>DISPLAY</c> syntax: <c>[host]:display[.screen]</c>, or <c>unix:display[.screen]</c> for an
    /// explicit local Unix-socket display. An empty/missing host (<c>:0</c>, the common case for a
    /// real local X session) means "local Unix domain socket" on Linux/macOS — matches every real X
    /// client's own resolution rule. Windows has no Unix-socket X convention, so an empty host there
    /// resolves to loopback TCP instead (matches VcXsrv/Xming/X410's default listening behavior).
    /// </remarks>
    public static X11DisplayEndpoint Resolve(string? displayEnvironmentVariable, bool isWindows)
    {
        var raw = string.IsNullOrWhiteSpace(displayEnvironmentVariable) ? DefaultDisplay(isWindows) : displayEnvironmentVariable.Trim();

        // "unix:display[.screen]" has no host part at all (unlike "[host]:display[.screen]") — the
        // colon here separates the "unix" literal from the display number, not host from display.
        var isExplicitUnix = raw.StartsWith("unix:", StringComparison.OrdinalIgnoreCase);
        string host, displayAndScreen;
        if (isExplicitUnix)
        {
            host = "";
            displayAndScreen = raw["unix:".Length..];
        }
        else
        {
            var colonIndex = raw.LastIndexOf(':');
            if (colonIndex < 0)
                throw new FormatException($"Invalid DISPLAY value '{raw}' — expected '[host]:display[.screen]'.");
            host = raw[..colonIndex];
            displayAndScreen = raw[(colonIndex + 1)..];
        }

        var dotIndex = displayAndScreen.IndexOf('.');
        var displayNumber = int.Parse(dotIndex < 0 ? displayAndScreen : displayAndScreen[..dotIndex], System.Globalization.CultureInfo.InvariantCulture);
        var screenNumber = dotIndex < 0 ? 0 : int.Parse(displayAndScreen[(dotIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var useUnixSocket = !isWindows && (isExplicitUnix || string.IsNullOrEmpty(host) || host == "localhost");

        EndPoint endpoint = useUnixSocket
            ? new UnixDomainSocketEndPoint($"/tmp/.X11-unix/X{displayNumber}")
            : new IPEndPoint(ResolveHost(host), 6000 + displayNumber);

        return new X11DisplayEndpoint(endpoint, displayNumber, screenNumber);
    }

    /// <summary>
    /// No <c>DISPLAY</c> set: Windows has no default local X session, so this guesses the common
    /// dev-machine case (VcXsrv/Xming/X410 already running, listening on its default TCP port,
    /// display 0) rather than failing outright — matches the spec's "PolyTerm detects, configures
    /// DISPLAY" intent for the common case, though it's a guess, not a real detection.
    /// Linux/macOS default to display 0 on the local Unix socket, the standard convention.
    /// </summary>
    private static string DefaultDisplay(bool isWindows) => isWindows ? "127.0.0.1:0" : ":0";

    private static IPAddress ResolveHost(string host) =>
        string.IsNullOrEmpty(host) ? IPAddress.Loopback : IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host)[0];

    public static X11DisplayEndpoint ResolveFromEnvironment() =>
        Resolve(Environment.GetEnvironmentVariable("DISPLAY"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>
    /// Best-effort check that something is actually listening at <see cref="Endpoint"/> — a plain TCP/
    /// Unix-socket connect attempt, no X11 handshake at all, so it works the same way regardless of
    /// which X server is running. False on any failure (connection refused, timeout, unreachable host)
    /// as well as any unexpected exception — this is a UI hint ("should I show install guidance?"),
    /// not something callers should treat as a hard guarantee either way.
    /// </summary>
    public bool IsReachable(TimeSpan timeout)
    {
        try
        {
            using var socket = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.IP);
            var connectTask = socket.ConnectAsync(Endpoint);
            return connectTask.Wait(timeout) && socket.Connected;
        }
        catch
        {
            return false;
        }
    }
}
