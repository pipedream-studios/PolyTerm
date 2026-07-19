using Microsoft.Extensions.Logging;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// Describes the FreeRDP-backed RDP protocol module (issue #67) — Linux/macOS's counterpart to
/// <c>PolyTerm.Protocols.Rdp</c>'s Windows-only MSTSC ActiveX implementation. Capability-gated on
/// whether the native shim actually loads (<see cref="NativeMethods.TryLoadLibrary"/>), per
/// docs/polyterm-spec.md §5.2's containment rule: "RDP is capability-gated and pack-optional. If a
/// platform's native pack is missing or broken, PolyTerm runs with RDP greyed-out and says why."
/// </summary>
public sealed class RdpFreerdpProtocolDescriptor : IProtocolDescriptor
{
    private readonly ILoggerFactory _loggerFactory;

    public RdpFreerdpProtocolDescriptor(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Id => "rdp";
    public string DisplayName => "RDP";
    public string Icon => "🖥️";
    public int DefaultPort => 3389;
    public SurfaceKind Surface => SurfaceKind.Framebuffer;
    public ProtocolCapabilities Capabilities => ProtocolCapabilities.GraphicalDesktop;
    public SettingsSchema Settings => SettingsSchema.Empty;

    public bool IsAvailable(out string? reason)
    {
        if (NativeMethods.TryLoadLibrary())
        {
            reason = null;
            return true;
        }
        reason = "RDP requires the FreeRDP native shim (libpolyterm_rdp_shim), which is missing or failed to load on this platform.";
        return false;
    }

    public IProtocolSession CreateSession(SessionContext ctx)
    {
        if (!IsAvailable(out var reason))
            throw new PlatformNotSupportedException(reason);
        return new RdpFreerdpSession(ctx, _loggerFactory);
    }
}
