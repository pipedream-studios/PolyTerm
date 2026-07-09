using Microsoft.Extensions.DependencyInjection;
using PolyTerm.Engine;

namespace PolyTerm.Protocols.Ssh;

/// <summary>DI registration for the SSH protocol module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SshProtocolDescriptor"/> as an <see cref="IProtocolDescriptor"/>.</summary>
    public static IServiceCollection AddPolyTermSsh(this IServiceCollection services)
    {
        services.AddSingleton<IProtocolDescriptor, SshProtocolDescriptor>();
        return services;
    }
}
