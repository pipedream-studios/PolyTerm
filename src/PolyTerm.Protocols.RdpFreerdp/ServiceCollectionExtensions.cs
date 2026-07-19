using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// DI registration for the FreeRDP-backed RDP protocol module (issue #67). Not yet wired into
/// <c>AppCompositionRoot</c> — deciding how this coexists with the Windows MSTSC-based
/// <c>PolyTerm.Protocols.Rdp</c> in one running app (and unblocking <c>PolyTerm.App</c>'s own
/// <c>net10.0-windows</c> TargetFramework) is a separate, later piece of work.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="RdpFreerdpProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermRdpFreerdp(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, RdpFreerdpProtocolDescriptor>();
        return services;
    }
}
