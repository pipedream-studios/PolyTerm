using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Serial;

/// <summary>DI registration for the Serial/COM protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SerialProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermSerial(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SerialProtocolDescriptor>();
        return services;
    }
}
