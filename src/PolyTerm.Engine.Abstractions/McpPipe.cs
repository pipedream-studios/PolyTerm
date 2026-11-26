using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PolyTerm.Engine;

/// <summary>
/// How the app's MCP bridge and its clients (<c>polyterm-mcp</c> and <c>polyterm attach</c>) agree on a pipe.
/// All of them go through here, so they find each other by default.
/// <para/>
/// Each user gets their own pipe (issue #378). On Linux a plain pipe name becomes a socket in <c>/tmp</c>, which
/// all users share, and on Windows pipe names are shared by the whole machine, so one fixed name let the first
/// user's app (or its leftover socket) stop everyone else's bridge from starting.
/// </summary>
public static class McpPipe
{
    /// <summary>Overrides the pipe name for the clients, and for the app unless <see cref="AppVariable"/> is set.</summary>
    public const string ClientVariable = "POLYTERM_PIPE";

    /// <summary>Overrides the pipe name for the app only.</summary>
    public const string AppVariable = "POLYTERM_MCP_PIPE_NAME";

    private const string BaseName = "polyterm";
    private const string LinuxRuntimeRoot = "/run/user";

    /// <summary>The pipe name the app serves on.</summary>
    public static string ResolveForApp() => Override(AppVariable) ?? ResolveForClient();

    /// <summary>The pipe name a client connects to.</summary>
    public static string ResolveForClient() => Override(ClientVariable) ?? DefaultName();

    /// <summary>
    /// The current user's pipe name. On Linux, a socket path in the user's private runtime folder (see
    /// <see cref="LinuxName"/>). On Windows, <c>polyterm-&lt;user SID&gt;</c>. On macOS, <c>polyterm</c>: the socket
    /// goes in <c>$TMPDIR</c>, which macOS already makes per-user, and socket paths there have little room to spare.
    /// </summary>
    public static string DefaultName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"{BaseName}-{identity.User!.Value}";
        }

        return OperatingSystem.IsLinux()
            ? LinuxName(ParseEffectiveUserId(File.ReadLines("/proc/self/status")), LinuxRuntimeRoot)
            : BaseName;
    }

    /// <summary>
    /// <c>/run/user/&lt;uid&gt;/polyterm.sock</c>: .NET uses a rooted pipe name as the socket path itself, and no
    /// other user can create anything in that folder. Built from the user ID rather than <c>XDG_RUNTIME_DIR</c>,
    /// because some MCP hosts start <c>polyterm-mcp</c> with a trimmed environment and both ends must agree.
    /// Without the folder (no logind, some containers) it falls back to the name <c>polyterm-&lt;uid&gt;</c>.
    /// </summary>
    internal static string LinuxName(uint userId, string runtimeRoot)
    {
        var id = userId.ToString(CultureInfo.InvariantCulture);
        var runtimeDirectory = Path.Combine(runtimeRoot, id);
        return Directory.Exists(runtimeDirectory)
            ? Path.Combine(runtimeDirectory, BaseName + ".sock")
            : $"{BaseName}-{id}";
    }

    /// <summary>
    /// Creates a client for the bridge. Connect it, then call <see cref="EnsureServerIsCurrentUser"/> before sending
    /// anything. Outside Windows, <see cref="PipeOptions.CurrentUserOnly"/> already makes connecting fail if another
    /// user is serving the pipe.
    /// </summary>
    public static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut,
            OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// Windows only (elsewhere it does nothing, see <see cref="CreateClient"/>): throws unless the connected pipe
    /// belongs to the current user. <see cref="PipeOptions.CurrentUserOnly"/> isn't used for this, because it only
    /// accepts the process's default owner. For an elevated process that's the Administrators group, so an elevated
    /// terminal would refuse the same user's non-elevated PolyTerm.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Another user is serving the pipe.</exception>
    public static void EnsureServerIsCurrentUser(NamedPipeClientStream pipe)
    {
        if (OperatingSystem.IsWindows())
            EnsureWindowsOwner(pipe);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwner(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        using var identity = WindowsIdentity.GetCurrent();
        if (owner is null || (!owner.Equals(identity.User) && !owner.Equals(identity.Owner)))
            throw new UnauthorizedAccessException("Could not connect to the pipe because it was not owned by the current user.");
    }

    private static string? Override(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Picks the effective user ID out of <c>/proc/self/status</c>, whose <c>Uid:</c> line lists the real,
    /// effective, saved and filesystem IDs. .NET has no API for it, and this avoids a native call.
    /// </summary>
    internal static uint ParseEffectiveUserId(IEnumerable<string> statusLines)
    {
        var fields = statusLines.First(line => line.StartsWith("Uid:", StringComparison.Ordinal))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return uint.Parse(fields[2], CultureInfo.InvariantCulture);
    }
}
