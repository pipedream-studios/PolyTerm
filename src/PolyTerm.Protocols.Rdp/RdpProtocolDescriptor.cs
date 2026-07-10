using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Rdp;

/// <summary>
/// Describes the RDP protocol module's capabilities and settings, and creates <see cref="RdpSession"/>
/// instances. Backed by the OS's own MSTSC ActiveX control (<c>mstscax.dll</c>) — Windows-only until a
/// cross-platform (FreeRDP-based) shim exists; see <see cref="IsAvailable"/>.
/// </summary>
public sealed class RdpProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public RdpProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "rdp";
    public string DisplayName => "RDP";
    public int DefaultPort => 3389;
    public SurfaceKind Surface => SurfaceKind.NativeHost;

    /// <summary>RDP is a graphical desktop session; no terminal, file transfer, or port forwarding.</summary>
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.GraphicalDesktop;

    public SettingsSchema Settings { get; } = BuildSchema();

    /// <summary>False on non-Windows hosts — the native MSTSC ActiveX control this module wraps isn't available there yet.</summary>
    public bool IsAvailable(out string? reason)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reason = null;
            return true;
        }

        reason = "RDP requires Windows (native control not yet available on this OS).";
        return false;
    }

    /// <summary>Creates a new, not-yet-connected <see cref="RdpSession"/> bound to the given context.</summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows. The App should have already
    /// gated the connect affordance via <see cref="IsAvailable"/> — this is a defensive fallback.</exception>
    public IProtocolSession CreateSession(SessionContext ctx)
    {
        if (!IsAvailable(out var reason))
            throw new PlatformNotSupportedException(reason);

        return new RdpSession(ctx, _loggerFactory.CreateLogger<RdpSession>());
    }

    private static SettingsSchema BuildSchema() => new()
    {
        Fields =
        [
            new() { Key = "username", DisplayName = "Username", Type = SettingsFieldType.Text, Required = true, Group = "Connection" },
            new() { Key = "domain",   DisplayName = "Domain",   Type = SettingsFieldType.Text, Group = "Connection" },
            new() { Key = "redirectDrives", DisplayName = "Redirect local drives", Type = SettingsFieldType.Boolean,
                    Description = "Makes local drives available inside the remote session as network locations.",
                    Group = "Redirection", Default = JsonSerializer.SerializeToElement(false) },
            new() { Key = "audioMode", DisplayName = "Remote audio", Type = SettingsFieldType.Enum,
                    Description = "Local, Remote, Disabled", Group = "Redirection",
                    Default = JsonSerializer.SerializeToElement("Local") },
            new() { Key = "redirectMicrophone", DisplayName = "Redirect microphone", Type = SettingsFieldType.Boolean,
                    Description = "Lets the remote session record from this computer's microphone.",
                    Group = "Redirection", Default = JsonSerializer.SerializeToElement(false) },
            new() { Key = "redirectDirectX", DisplayName = "Redirect DirectX/RemoteFX", Type = SettingsFieldType.Boolean,
                    Description = "Enables DirectX/RemoteFX graphics redirection, if the server supports it.",
                    Group = "Redirection", Default = JsonSerializer.SerializeToElement(false) },
        ]
    };
}
