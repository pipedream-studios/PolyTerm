using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Rdp;

/// <summary>DI registration for the RDP protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="RdpProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermRdp(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, RdpProtocolDescriptor>();
        return services;
    }
}
