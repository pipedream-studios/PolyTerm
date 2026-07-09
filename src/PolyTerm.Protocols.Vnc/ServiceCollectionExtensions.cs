using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Vnc;

/// <summary>DI registration for the VNC protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="VncProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermVnc(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, VncProtocolDescriptor>();
        return services;
    }
}
