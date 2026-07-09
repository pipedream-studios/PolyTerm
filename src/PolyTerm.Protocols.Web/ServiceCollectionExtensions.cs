using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Web;

/// <summary>DI registration for the HTTP/S launcher protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="HttpProtocolDescriptor"/> and <see cref="HttpsProtocolDescriptor"/> as <see cref="IProtocolDescriptor"/>s.</summary>
    public static IServiceCollection AddPolyTermWeb(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, HttpProtocolDescriptor>();
        services.AddSingleton<IProtocolDescriptor, HttpsProtocolDescriptor>();
        return services;
    }
}
