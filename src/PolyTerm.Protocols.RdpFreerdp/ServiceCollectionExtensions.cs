using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.RdpFreerdp;

/// <summary>
/// DI registration for the FreeRDP-backed RDP protocol module (issue #67). Wired into
/// <c>AppCompositionRoot</c> as of issue #84, which also removed the Windows MSTSC-based
/// <c>PolyTerm.Protocols.Rdp</c> module this replaced — FreeRDP is now the only RDP implementation,
/// on every platform.
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
