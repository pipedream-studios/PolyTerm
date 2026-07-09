using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.LocalShell;

/// <summary>DI registration for the local shell protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="LocalShellDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermLocalShell(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, LocalShellDescriptor>();
        return services;
    }
}
